import SwiftUI

struct RGB: Codable, Equatable {
    var R: UInt8 = 70; var G: UInt8 = 100; var B: UInt8 = 255
    var color: Color { Color(red: Double(R)/255, green: Double(G)/255, blue: Double(B)/255) }
    var hex: String { String(format: "#%02X%02X%02X", R, G, B) }
    init() {}
    init(color: Color) {
        let c = NSColor(color).usingColorSpace(.deviceRGB) ?? .white
        R = UInt8((c.redComponent * 255).rounded()); G = UInt8((c.greenComponent * 255).rounded()); B = UInt8((c.blueComponent * 255).rounded())
    }
}
struct AudioProfile: Codable, Equatable {
    var Brightness = 65.0; var Gain = 8.0; var Low = 100.0; var Mid = 100.0; var High = 100.0
    var FlashBrightness = 100.0; var FlashMilliseconds = 90.0; var Style = 0
    var OutputId = ""; var OutputName = ""
}
struct Device: Codable, Equatable, Identifiable {
    var Ip = ""; var Name = ""; var Selected = true; var `Protocol` = 0
    var DeviceId = ""; var ProtectedKey = ""; var TuyaVersion = "3.3"; var TuyaDevice22 = false
    var PowerDp = 0; var ModeDp = 0; var ColorDp = 0; var BluetoothRandom = false
    var BluetoothElk13 = false
    var id: String { Ip }
    var title: String { Name.isEmpty ? Ip : Name }
    var family: String { protocols.first(where: { $0.0 == self.Protocol })?.1 ?? "Magic Home" }
}
struct Settings: Codable, Equatable {
    var Schema = 1; var Devices = [Device](); var Color = RGB(); var Brightness = 65.0
    var TransitionBrightness = 65.0; var TransitionSeconds = 12.0
    var Music = AudioProfile(); var Spectrum = AudioProfile(); var AmbientBrightness = 65.0; var Display = ""
}
let protocols: [(Int, String)] = [(0,"Magic Home · автоматически"),(1,"Magic Home RGB8"),(2,"Magic Home RGB9"),(3,"Magic Home Legacy"),(4,"Magic Home RGBW"),(5,"Magic Home SPI2"),(6,"Magic Home SPI3"),(7,"Magic Home HSV25"),(8,"WLED"),(9,"Yeelight"),(10,"ELK-BLEDOM"),(11,"Triones"),(12,"Tuya / Smart Life")]
enum Page: Int, CaseIterable, Identifiable {
    case color, transition, music, spectrum, ambient, devices
    var id: Int { rawValue }
    var title: String { ["Цвет", "Переход", "Цветомузыка", "Спектр", "Фон", "Контроллеры"][rawValue] }
    var symbol: String { ["paintpalette", "circle.lefthalf.filled", "waveform", "chart.bar.xaxis", "display", "lightbulb.2"][rawValue] }
    var subtitle: String { ["Выберите цвет вашей комнаты", "Плавная смена оттенков", "Свет в ритме вашей музыки", "Низкие — красный, средние — синий, высокие — зелёный", "Свет, который продолжает ваш экран", "Wi-Fi и Bluetooth в одной комнате"][rawValue] }
}
struct Display: Codable, Identifiable { var Id: String; var Name: String; var id: String { Id } }

@MainActor final class RoomStore: ObservableObject {
    @Published var settings = Settings()
    @Published var page: Page? = .color
    @Published var activeMode = 0
    @Published var output = RGB()
    @Published var levels = [0.0, 0, 0]
    @Published var statuses = [String: String]()
    @Published var displays = [Display]()
    @Published var error: String?
    @Published var scanning = false
    @Published var connected = false
    @Published var power = true
    @Published var ready = false
    private var process: Process?
    private var input: FileHandle?
    private var saveTask: Task<Void, Never>?
    private let readQueue = DispatchQueue(label: "RGBroom.engine.output")
    func start() {
        guard process == nil else { return }
        let p = Process(); let stdin = Pipe(); let stdout = Pipe(); let stderr = Pipe()
        p.executableURL = Bundle.main.bundleURL.appendingPathComponent("Contents/Resources/Engine/RGBroom.Engine")
        p.standardInput = stdin; p.standardOutput = stdout; p.standardError = stderr
        // Never place keys in process arguments or diagnostic output.
        stderr.fileHandleForReading.readabilityHandler = { handle in _ = handle.availableData }
        p.terminationHandler = { [weak self] p in Task { @MainActor in self?.ready = false; self?.connected = false; if p.terminationStatus != 0 { self?.error = "Движок RGBroom завершился с кодом \(p.terminationStatus). Перезапустите приложение." } } }
        do { try p.run(); process = p; input = stdin.fileHandleForWriting }
        catch { self.error = "Не удалось запустить движок: \(error.localizedDescription)"; return }
        readQueue.async { [weak self] in
            var pending = Data()
            while true {
                let data = stdout.fileHandleForReading.availableData
                if data.isEmpty { break }; pending.append(data)
                while let end = pending.firstIndex(of: 10) {
                    let line = Data(pending[..<end]); pending.removeSubrange(...end)
                    Task { @MainActor in self?.receive(line) }
                }
            }
        }
    }
    func send(_ object: [String: Any]) {
        guard let input else { return }
        do { var data = try JSONSerialization.data(withJSONObject: object); data.append(10); try input.write(contentsOf: data) }
        catch { self.error = "Не удалось передать команду: \(error.localizedDescription)" }
    }
    func apply() {
        guard ready, let data = try? JSONEncoder().encode(settings), let object = try? JSONSerialization.jsonObject(with: data) else { return }
        send(["op":"apply", "mode":activeMode, "settings":object])
    }
    func scheduleSave() {
        saveTask?.cancel()
        saveTask = Task { try? await Task.sleep(for: .milliseconds(120)); if !Task.isCancelled { apply() } }
    }
    func connect() { apply(); send(["op":"connect"]); connected = true }
    func disconnect() { send(["op":"disconnect"]); connected = false; statuses.removeAll() }
    func scan(ble: Bool) { scanning = true; send(["op":"scan", "ble":ble]) }
    func saveKey(_ key: String, device: Device) { apply(); send(["op":"secret", "id":device.Ip, "key":key]) }
    func stop() { saveTask?.cancel(); apply(); send(["op":"quit"]); try? input?.close(); input = nil }
    private func receive(_ data: Data) {
        guard let r = try? JSONSerialization.jsonObject(with: data) as? [String: Any], let type = r["type"] as? String else { return }
        func decode<T: Decodable>(_ name: String, _ t: T.Type) -> T? { guard let v = r[name], let d = try? JSONSerialization.data(withJSONObject: v) else { return nil }; return try? JSONDecoder().decode(t, from:d) }
        switch type {
        case "settings": if let s = decode("settings", Settings.self) { settings = s; ready = true; send(["op":"displays"]) }
        case "frame": if let c = decode("color", RGB.self) { output = c }; levels = r["levels"] as? [Double] ?? [0,0,0]
        case "status": if let id = r["id"] as? String { statuses[id] = r["status"] as? String }
        case "error": error = r["message"] as? String; scanning = false
        case "displays": displays = decode("displays", [Display].self) ?? []
        case "discovered":
            for device in decode("devices", [Device].self) ?? [] where !settings.Devices.contains(where: { $0.Ip == device.Ip }) { settings.Devices.append(device) }
            scanning = false; apply()
        case "done": if r["op"] as? String == "scan" { scanning = false }
        default: break
        }
    }
}
