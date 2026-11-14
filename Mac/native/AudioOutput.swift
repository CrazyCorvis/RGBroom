import Foundation
import CoreAudio

private func audioError(_ message: String, _ status: OSStatus = noErr) -> NSError {
    NSError(domain: "RGBroom.Audio", code: Int(status), userInfo: [NSLocalizedDescriptionKey: message + (status == noErr ? "" : " (Core Audio: \(status))")])
}
private func address(_ selector: AudioObjectPropertySelector, _ scope: AudioObjectPropertyScope = kAudioObjectPropertyScopeGlobal) -> AudioObjectPropertyAddress {
    AudioObjectPropertyAddress(mSelector: selector, mScope: scope, mElement: kAudioObjectPropertyElementMain)
}
private func audioIDs(_ object: AudioObjectID, _ selector: AudioObjectPropertySelector, _ scope: AudioObjectPropertyScope = kAudioObjectPropertyScopeGlobal) throws -> [AudioObjectID] {
    var property = address(selector, scope), size: UInt32 = 0
    var status = AudioObjectGetPropertyDataSize(object, &property, 0, nil, &size)
    guard status == noErr else { throw audioError("Не удалось получить аудиоустройства", status) }
    if size == 0 { return [] }
    var ids = [AudioObjectID](repeating: 0, count: Int(size) / MemoryLayout<AudioObjectID>.stride)
    status = ids.withUnsafeMutableBytes { AudioObjectGetPropertyData(object, &property, 0, nil, &size, $0.baseAddress!) }
    guard status == noErr else { throw audioError("Не удалось прочитать аудиоустройства", status) }
    return Array(ids.prefix(Int(size) / MemoryLayout<AudioObjectID>.stride))
}
private func audioString(_ object: AudioObjectID, _ selector: AudioObjectPropertySelector) throws -> String {
    var property = address(selector), size = UInt32(MemoryLayout<CFString?>.size)
    var value: CFString?
    let status = withUnsafeMutablePointer(to: &value) { AudioObjectGetPropertyData(object, &property, 0, nil, &size, $0) }
    guard status == noErr, let value else { throw audioError("Не удалось прочитать имя аудиоустройства", status) }
    return value as String
}
struct AudioOutput {
    let object: AudioObjectID
    let uid: String
    let name: String
    static func available() throws -> [AudioOutput] {
        try audioIDs(AudioObjectID(kAudioObjectSystemObject), kAudioHardwarePropertyDevices).compactMap { device in
            guard !(try audioIDs(device, kAudioDevicePropertyStreams, kAudioDevicePropertyScopeOutput)).isEmpty else { return nil }
            let uid = try audioString(device, kAudioDevicePropertyDeviceUID)
            guard !uid.hasPrefix("com.crazycorvis.rgbroom.capture.") else { return nil }
            return AudioOutput(object: device, uid: uid, name: try audioString(device, kAudioObjectPropertyName))
        }.sorted { $0.name.localizedStandardCompare($1.name) == .orderedAscending }
    }
}

// A private, unmuted tap observes only the selected output. It never changes the default device.
final class OutputTap {
    private var tap: AudioObjectID = 0
    private var aggregate: AudioObjectID = 0
    private var ioProc: AudioDeviceIOProcID?
    private var output: AudioObjectID = 0
    private let queue = DispatchQueue(label: "RGBroom.output-audio")
    private(set) var sampleRate = 48000.0
    func start(uid: String, receive: @escaping (UnsafePointer<AudioBufferList>) -> Void) throws {
        guard #available(macOS 14.2, *) else { throw audioError("Выбор аудиовыхода требует macOS 14.2 или новее. Выберите общий системный звук.") }
        guard let device = try AudioOutput.available().first(where: { $0.uid == uid }) else {
            throw audioError("Выбранный аудиовыход недоступен. Подключите его или выберите другое устройство.")
        }
        output = device.object
        do {
            let description = CATapDescription(excludingProcesses: [], deviceUID: uid, stream: 0)
            description.name = "RGBroom · \(device.name)"
            description.isPrivate = true
            description.muteBehavior = .unmuted
            try check(AudioHardwareCreateProcessTap(description, &tap), "Не удалось начать захват. Разрешите RGBroom запись системного аудио в Системных настройках.")
            var format = AudioStreamBasicDescription()
            var property = address(kAudioTapPropertyFormat), size = UInt32(MemoryLayout<AudioStreamBasicDescription>.size)
            try check(AudioObjectGetPropertyData(tap, &property, 0, nil, &size, &format), "Не удалось прочитать формат аудиовыхода")
            guard format.mFormatID == kAudioFormatLinearPCM, format.mFormatFlags & kAudioFormatFlagIsFloat != 0,
                  format.mFormatFlags & kAudioFormatFlagIsBigEndian == 0, format.mBitsPerChannel == 32,
                  format.mChannelsPerFrame > 0, format.mSampleRate.isFinite, format.mSampleRate > 0 else {
                throw audioError("Формат этого аудиовыхода не поддерживается. Выберите общий системный звук.")
            }
            sampleRate = format.mSampleRate
            let tapUID = try audioString(tap, kAudioTapPropertyUID)
            let configuration: [String: Any] = [
                kAudioAggregateDeviceNameKey: "RGBroom Audio Capture",
                kAudioAggregateDeviceUIDKey: "com.crazycorvis.rgbroom.capture.\(UUID().uuidString)",
                kAudioAggregateDeviceIsPrivateKey: true,
                kAudioAggregateDeviceSubDeviceListKey: [],
                kAudioAggregateDeviceTapAutoStartKey: true,
                kAudioAggregateDeviceTapListKey: [[kAudioSubTapUIDKey: tapUID, kAudioSubTapDriftCompensationKey: true]]
            ]
            try check(AudioHardwareCreateAggregateDevice(configuration as CFDictionary, &aggregate), "Не удалось создать захват аудиовыхода")
            // Tap-only aggregate: no microphone inputs and no playback routing changes.
            try check(AudioDeviceCreateIOProcIDWithBlock(&ioProc, aggregate, queue) { _, input, _, _, _ in
                receive(input)
            }, "Не удалось подключить обработчик аудио")
            try check(AudioDeviceStart(aggregate, ioProc), "Не удалось запустить захват. Проверьте разрешение записи системного аудио.")
        } catch { stop(); throw error }
    }
    var isAvailable: Bool {
        var property = address(kAudioDevicePropertyDeviceIsAlive), size = UInt32(MemoryLayout<UInt32>.size), alive: UInt32 = 0
        guard AudioObjectGetPropertyData(output, &property, 0, nil, &size, &alive) == noErr, alive != 0 else { return false }
        var rateProperty = address(kAudioDevicePropertyNominalSampleRate), rateSize = UInt32(MemoryLayout<Double>.size), rate = 0.0
        return AudioObjectGetPropertyData(output, &rateProperty, 0, nil, &rateSize, &rate) == noErr && rate == sampleRate
    }
    func stop() {
        if let ioProc { AudioDeviceStop(aggregate, ioProc); AudioDeviceDestroyIOProcID(aggregate, ioProc) }
        ioProc = nil
        queue.sync { }
        if aggregate != 0 { AudioHardwareDestroyAggregateDevice(aggregate); aggregate = 0 }
        if #available(macOS 14.2, *), tap != 0 { AudioHardwareDestroyProcessTap(tap); tap = 0 }
    }
    deinit { stop() }
    private func check(_ status: OSStatus, _ message: String) throws { if status != noErr { throw audioError(message, status) } }
}
