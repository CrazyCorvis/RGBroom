import Foundation
import ScreenCaptureKit
import CoreMedia
import CoreBluetooth
import Security
import CoreGraphics

private func failure(_ text: String) -> NSError { NSError(domain: "RGBroom", code: 1, userInfo: [NSLocalizedDescriptionKey: text]) }
private let registryLock = NSLock()
private var captures: [Int32: Capture] = [:]
private var connections: [Int32: BLEConnection] = [:]
private var serial: Int32 = 0
private func nextID() -> Int32 { registryLock.lock(); defer { registryLock.unlock() }; serial += 1; return serial }
private func capture(_ id: Int32) -> Capture? { registryLock.lock(); defer { registryLock.unlock() }; return captures[id] }
private func connection(_ id: Int32) throws -> BLEConnection { registryLock.lock(); defer { registryLock.unlock() }; guard let c = connections[id] else { throw failure("BLE-соединение закрыто") }; return c }

private final class Capture: NSObject, SCStreamOutput, SCStreamDelegate {
    let lock = NSLock()
    let queue = DispatchQueue(label: "RGBroom.capture")
    var stream: SCStream?
    var pixels = [UInt8](repeating: 0, count: 128 * 72 * 4)
    var samples = [[Float]](repeating: [Float](repeating: 0, count: 2048), count: 2)
    var position = 0
    var lastAudio = Date.distantPast
    var failed = false
    func start(display: String, audio: Bool) throws {
        let done = DispatchSemaphore(value: 0)
        var result: Error?
        Task {
            do {
                let content = try await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: true)
                guard let selected = display.isEmpty ? content.displays.first(where: { $0.displayID == CGMainDisplayID() }) ?? content.displays.first : content.displays.first(where: { String($0.displayID) == display }) else { throw failure("Выбранный дисплей недоступен") }
                let configuration = SCStreamConfiguration()
                configuration.width = 128; configuration.height = 72
                configuration.pixelFormat = kCVPixelFormatType_32BGRA
                configuration.minimumFrameInterval = CMTime(value: 1, timescale: audio ? 2 : 15)
                configuration.queueDepth = 3; configuration.showsCursor = false
                configuration.capturesAudio = audio; configuration.sampleRate = 48000; configuration.channelCount = 2
                configuration.excludesCurrentProcessAudio = true
                let s = SCStream(filter: SCContentFilter(display: selected, excludingWindows: []), configuration: configuration, delegate: self)
                try s.addStreamOutput(self, type: .screen, sampleHandlerQueue: queue)
                if audio { try s.addStreamOutput(self, type: .audio, sampleHandlerQueue: queue) }
                stream = s
                try await s.startCapture()
            } catch { result = error }
            done.signal()
        }
        // The caller runs in the .NET worker, never the AppKit main thread.
        done.wait()
        if let error = result { throw failure("Разрешите RGBroom запись экрана и системного аудио в Системных настройках → Конфиденциальность и безопасность. \(error.localizedDescription)") }
    }
    func stop() {
        let done = DispatchSemaphore(value: 0)
        Task { try? await stream?.stopCapture(); done.signal() }
        done.wait(); stream = nil
    }
    func stream(_ stream: SCStream, didStopWithError error: Error) { lock.lock(); failed = true; lock.unlock() }
    func stream(_ stream: SCStream, didOutputSampleBuffer sampleBuffer: CMSampleBuffer, of type: SCStreamOutputType) {
        guard sampleBuffer.isValid else { return }
        if type == .screen, let buffer = sampleBuffer.imageBuffer {
            guard CVPixelBufferGetWidth(buffer) == 128, CVPixelBufferGetHeight(buffer) == 72 else { return }
            CVPixelBufferLockBaseAddress(buffer, .readOnly)
            defer { CVPixelBufferUnlockBaseAddress(buffer, .readOnly) }
            guard let base = CVPixelBufferGetBaseAddress(buffer) else { return }
            let stride = CVPixelBufferGetBytesPerRow(buffer)
            lock.lock(); defer { lock.unlock() }
            pixels.withUnsafeMutableBytes { dest in
                for y in 0..<72 { memcpy(dest.baseAddress!.advanced(by: y * 512), base.advanced(by: y * stride), 512) }
            }
        } else if type == .audio {
            guard let format = sampleBuffer.formatDescription,
                  let asbd = CMAudioFormatDescriptionGetStreamBasicDescription(format),
                  asbd.pointee.mFormatFlags & kAudioFormatFlagIsFloat != 0,
                  asbd.pointee.mBitsPerChannel == 32 else { return }
            var needed = 0
            CMSampleBufferGetAudioBufferListWithRetainedBlockBuffer(sampleBuffer, bufferListSizeNeededOut: &needed, bufferListOut: nil, bufferListSize: 0, blockBufferAllocator: nil, blockBufferMemoryAllocator: nil, flags: 0, blockBufferOut: nil)
            guard needed > 0 else { return }
            let raw = UnsafeMutableRawPointer.allocate(byteCount: needed, alignment: MemoryLayout<AudioBufferList>.alignment)
            defer { raw.deallocate() }
            let list = raw.bindMemory(to: AudioBufferList.self, capacity: 1)
            var block: CMBlockBuffer?
            guard CMSampleBufferGetAudioBufferListWithRetainedBlockBuffer(sampleBuffer, bufferListSizeNeededOut: nil, bufferListOut: list, bufferListSize: needed, blockBufferAllocator: nil, blockBufferMemoryAllocator: nil, flags: 0, blockBufferOut: &block) == noErr else { return }
            let buffers = UnsafeMutableAudioBufferListPointer(list)
            let frames = sampleBuffer.numSamples
            lock.lock(); defer { lock.unlock() }
            for i in 0..<frames {
                for ch in 0..<2 {
                    let b = buffers[min(ch, buffers.count - 1)]
                    guard let data = b.mData else { continue }
                    let channels = Int(b.mNumberChannels)
                    let index = i * channels + (buffers.count == 1 ? min(ch, channels - 1) : 0)
                    if index * 4 + 4 <= Int(b.mDataByteSize) {
                        let v = data.assumingMemoryBound(to: Float.self)[index]
                        samples[ch][position] = v.isFinite ? v : 0
                    }
                }
                position = (position + 1) % 2048
            }
            lastAudio = Date()
        }
    }
}

private final class BLEConnection: NSObject, CBCentralManagerDelegate, CBPeripheralDelegate {
    let queue = DispatchQueue(label: "RGBroom.ble")
    var central: CBCentralManager!
    var peripheral: CBPeripheral?
    var characteristic: CBCharacteristic?
    var found: [UUID: [String: Any]] = [:]
    var pending: DispatchSemaphore?
    var error: Error?
    var triones = false
    var writePending = false
    override init() { super.init(); central = CBCentralManager(delegate: self, queue: queue) }
    func centralManagerDidUpdateState(_ central: CBCentralManager) { }
    func ready() throws {
        let deadline = Date().addingTimeInterval(10)
        while Date() < deadline {
            let state = queue.sync { central.state }
            if state == .poweredOn { return }
            if state == .unauthorized { throw failure("Разрешите RGBroom доступ к Bluetooth в Системных настройках") }
            if state == .poweredOff || state == .unsupported { throw failure("Включите Bluetooth на Mac") }
            Thread.sleep(forTimeInterval: 0.1)
        }
        throw failure("Bluetooth не готов")
    }
    func scan() throws -> [[String: Any]] {
        try ready()
        queue.sync { central.scanForPeripherals(withServices: nil) }
        Thread.sleep(forTimeInterval: 8)
        return queue.sync { central.stopScan(); return Array(found.values) }
    }
    func centralManager(_ central: CBCentralManager, didDiscover peripheral: CBPeripheral, advertisementData: [String: Any], rssi RSSI: NSNumber) {
        let name = advertisementData[CBAdvertisementDataLocalNameKey] as? String ?? peripheral.name ?? ""
        let n = name.uppercased()
        let triones = n.contains("TRIONES") || n.contains("LEDBLE") || n.contains("LED BLE")
        let elk = n.hasPrefix("ELK-BLEDOM") || n.hasPrefix("ELK-BLEDDM") || n == "BLEDOM" || n.hasPrefix("ELK-BLE_")
        if triones || elk { found[peripheral.identifier] = ["id": peripheral.identifier.uuidString, "name": name, "triones": triones] }
    }
    func connect(id: UUID, triones: Bool) throws {
        try ready()
        let done = DispatchSemaphore(value: 0)
        queue.sync {
            self.triones = triones; pending = done
            guard let p = central.retrievePeripherals(withIdentifiers: [id]).first else { error = failure("Устройство не найдено. Повторите поиск BLE на этом Mac"); done.signal(); return }
            peripheral = p; p.delegate = self; central.connect(p)
        }
        if done.wait(timeout: .now() + 20) == .timedOut { close(); throw failure("Истекло время подключения BLE") }
        if let error = queue.sync(execute: { error }) { close(); throw error }
    }
    func finish(_ e: Error? = nil) { error = e; pending?.signal(); pending = nil }
    func centralManager(_ central: CBCentralManager, didConnect peripheral: CBPeripheral) { peripheral.discoverServices([CBUUID(string: triones ? "FFD5" : "FFF0")]) }
    func centralManager(_ central: CBCentralManager, didFailToConnect peripheral: CBPeripheral, error: Error?) { finish(error ?? failure("Не удалось подключить BLE")) }
    func centralManager(_ central: CBCentralManager, didDisconnectPeripheral peripheral: CBPeripheral, error: Error?) { characteristic = nil; finish(error ?? failure("BLE-соединение потеряно")) }
    func peripheral(_ peripheral: CBPeripheral, didDiscoverServices error: Error?) {
        guard error == nil, let service = peripheral.services?.first else { finish(error ?? failure("Нет подходящего GATT-сервиса")); return }
        peripheral.discoverCharacteristics([CBUUID(string: triones ? "FFD9" : "FFF3")], for: service)
    }
    func peripheral(_ peripheral: CBPeripheral, didDiscoverCharacteristicsFor service: CBService, error: Error?) {
        guard error == nil, let c = service.characteristics?.first, c.properties.contains(.write) || c.properties.contains(.writeWithoutResponse) else { finish(error ?? failure("Нет характеристики записи BLE")); return }
        characteristic = c; finish()
    }
    func write(_ data: Data) throws {
        let done = DispatchSemaphore(value: 0)
        queue.sync {
            error = nil
            guard let p = peripheral, p.state == .connected, let c = characteristic else { error = failure("BLE-соединение потеряно"); done.signal(); return }
            if c.properties.contains(.write) {
                pending = done; writePending = true; p.writeValue(data, for: c, type: .withResponse)
            } else if p.canSendWriteWithoutResponse {
                p.writeValue(data, for: c, type: .withoutResponse); done.signal()
            } else { error = failure("Очередь BLE заполнена"); done.signal() }
        }
        if done.wait(timeout: .now() + 8) == .timedOut { close(); throw failure("Истекло время записи BLE") }
        if let e = queue.sync(execute: { error }) { throw e }
    }
    func peripheral(_ peripheral: CBPeripheral, didWriteValueFor characteristic: CBCharacteristic, error: Error?) { if writePending { writePending = false; finish(error) } }
    func ping() throws { if !queue.sync(execute: { peripheral?.state == .connected && characteristic != nil }) { throw failure("BLE-контроллер отключён") } }
    func close() { queue.sync { if let p = peripheral { central.cancelPeripheralConnection(p) }; characteristic = nil; pending = nil } }
}

private func request(_ r: [String: Any]) throws -> [String: Any] {
    let op = r["op"] as? String ?? ""
    switch op {
    case "displays":
        var ids = [CGDirectDisplayID](repeating: 0, count: 32); var count: UInt32 = 0
        CGGetActiveDisplayList(32, &ids, &count)
        return ["displays": (0..<Int(count)).map { i in ["id": String(ids[i]), "name": "Дисплей \(i+1) · \(CGDisplayPixelsWide(ids[i]))×\(CGDisplayPixelsHigh(ids[i]))" + (ids[i] == CGMainDisplayID() ? " · основной" : "")] }]
    case "capture-start":
        let c = Capture(); try c.start(display: r["display"] as? String ?? "", audio: r["audio"] as? Bool ?? false)
        let id = nextID(); registryLock.lock(); captures[id] = c; registryLock.unlock(); return ["handle": id]
    case "capture-stop":
        let id = (r["handle"] as? NSNumber)?.int32Value ?? 0
        registryLock.lock(); let c = captures.removeValue(forKey: id); registryLock.unlock(); c?.stop(); return [:]
    case "ble-scan": let c = BLEConnection(); return ["devices": try c.scan()]
    case "ble-connect":
        guard let id = UUID(uuidString: r["id"] as? String ?? "") else { throw failure("Нужен UUID BLE-устройства") }
        let c = BLEConnection(); try c.connect(id: id, triones: r["triones"] as? Bool ?? false)
        let handle = nextID(); registryLock.lock(); connections[handle] = c; registryLock.unlock(); return ["handle": handle]
    case "ble-close":
        let id = (r["handle"] as? NSNumber)?.int32Value ?? 0
        registryLock.lock(); let c = connections.removeValue(forKey: id); registryLock.unlock(); c?.close(); return [:]
    case "ble-write", "ble-ping":
        let c = try connection((r["handle"] as? NSNumber)?.int32Value ?? 0)
        if op == "ble-ping" { try c.ping() } else {
            let hex = r["bytes"] as? String ?? ""; var bytes = [UInt8](); var index = hex.startIndex
            while index < hex.endIndex { let end = hex.index(index, offsetBy: 2, limitedBy: hex.endIndex) ?? hex.endIndex; guard let b = UInt8(hex[index..<end], radix: 16) else { throw failure("Некорректный BLE-пакет") }; bytes.append(b); index = end }
            try c.write(Data(bytes))
        }
        return [:]
    case "secret-save", "secret-read", "secret-delete":
        let id = op == "secret-save" ? UUID().uuidString : r["id"] as? String ?? ""
        var query: [String: Any] = [kSecClass as String: kSecClassGenericPassword, kSecAttrService as String: "com.crazycorvis.rgbroom.tuya", kSecAttrAccount as String: id]
        if op == "secret-save" {
            query[kSecValueData as String] = Data((r["value"] as? String ?? "").utf8)
            query[kSecAttrAccessible as String] = kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly
            let status = SecItemAdd(query as CFDictionary, nil)
            guard status == errSecSuccess else { throw failure("Keychain: \(status)") }; return ["id": id]
        }
        if op == "secret-delete" { let status = SecItemDelete(query as CFDictionary); guard status == errSecSuccess || status == errSecItemNotFound else { throw failure("Keychain: \(status)") }; return [:] }
        query[kSecReturnData as String] = true; query[kSecMatchLimit as String] = kSecMatchLimitOne
        var value: CFTypeRef?
        let status = SecItemCopyMatching(query as CFDictionary, &value)
        guard status == errSecSuccess, let data = value as? Data, let text = String(data: data, encoding: .utf8) else { throw failure("Не удалось прочитать local_key из Keychain (\(status)). Введите ключ заново.") }
        return ["value": text]
    default: throw failure("Неизвестная операция")
    }
}

@_cdecl("rgb_request") public func rgb_request(_ json: UnsafePointer<CChar>) -> UnsafeMutablePointer<CChar>? {
    var result: [String: Any]
    do { let r = try JSONSerialization.jsonObject(with: Data(String(cString: json).utf8)) as? [String: Any] ?? [:]; result = try request(r) }
    catch { result = ["error": error.localizedDescription] }
    let data = (try? JSONSerialization.data(withJSONObject: result, options: [.sortedKeys])) ?? Data("{}".utf8)
    return strdup(String(decoding: data, as: UTF8.self))
}
@_cdecl("rgb_free") public func rgb_free(_ value: UnsafeMutableRawPointer?) { free(value) }
@_cdecl("rgb_audio") public func rgb_audio(_ id: Int32, _ dest: UnsafeMutablePointer<Float>) -> Int32 {
    guard let c = capture(id) else { return -1 }; c.lock.lock(); defer { c.lock.unlock() }
    if c.failed { return -1 }
    let silent = Date().timeIntervalSince(c.lastAudio) > 0.15
    for ch in 0..<2 { for i in 0..<2048 { dest[ch * 2048 + i] = silent ? 0 : c.samples[ch][(c.position + i) % 2048] } }
    return 0
}
@_cdecl("rgb_screen") public func rgb_screen(_ id: Int32, _ dest: UnsafeMutablePointer<UInt8>) -> Int32 {
    guard let c = capture(id) else { return -1 }; c.lock.lock(); defer { c.lock.unlock() }
    if c.failed { return -1 }
    c.pixels.withUnsafeBufferPointer { dest.update(from: $0.baseAddress!, count: $0.count) }; return 0
}
