import SwiftUI

@main struct RGBroomApp: App {
    @StateObject private var store = RoomStore()
    var body: some Scene {
        WindowGroup {
            RoomView(store: store)
                .frame(minWidth: 780, minHeight: 600)
                .task { store.start() }
                .onReceive(NotificationCenter.default.publisher(for: NSApplication.willTerminateNotification)) { _ in store.stop() }
        }
        .defaultSize(width: 1020, height: 740)
        .commands {
            CommandGroup(after: .newItem) {
                Button("Подключить контроллеры") { store.connect() }.keyboardShortcut("k").disabled(!store.ready || store.settings.Devices.isEmpty)
                Button("Отключить контроллеры") { store.disconnect() }.disabled(!store.connected)
            }
            CommandMenu("Освещение") {
                ForEach(Page.allCases.filter { $0 != .devices }) { page in Button(page.title) { store.page = page }.keyboardShortcut(KeyEquivalent(Character(String(page.rawValue + 1)))) }
                Divider()
                Button(store.power ? "Выключить свет" : "Включить свет") { store.power.toggle(); store.send(["op":"power", "on":store.power]) }.keyboardShortcut("p")
            }
        }
        SwiftUI.Settings { PreferencesView().frame(width: 460, height: 240) }
    }
}

struct RoomView: View {
    @ObservedObject var store: RoomStore
    @State private var adding = false
    @State private var editing: Device?
    var body: some View {
        NavigationSplitView {
            List(selection: $store.page) {
                Section("Освещение") {
                    ForEach(Page.allCases.filter { $0 != .devices }) { page in Label(page.title, systemImage: page.symbol).tag(page) }
                }
                Section("Комната") { Label("Контроллеры", systemImage: "lightbulb.2").tag(Page.devices) }
            }
            .listStyle(.sidebar)
            .navigationSplitViewColumnWidth(min: 180, ideal: 210, max: 270)
            .safeAreaInset(edge: .bottom) {
                HStack(spacing: 9) {
                    Circle().fill(store.connected ? Color.green : Color.secondary.opacity(0.5)).frame(width: 7, height: 7)
                    VStack(alignment: .leading, spacing: 3) {
                        Text(store.connected ? "Комната подключена" : "Нет подключения").font(.callout)
                        Text("\(store.settings.Devices.filter(\.Selected).count) выбрано").font(.caption).foregroundStyle(.secondary)
                    }
                    Spacer()
                }.padding(16)
            }
        } detail: {
            Group {
                if store.page == .devices { devicesPage }
                else { modePage }
            }
            .navigationTitle((store.page ?? .color).title)
            .navigationSubtitle((store.page ?? .color).subtitle)
            .toolbar {
                ToolbarItemGroup(placement: .primaryAction) {
                    Button { if store.connected { store.disconnect() } else { store.connect() } } label: {
                        Label(store.connected ? "Отключить" : "Подключить", systemImage: store.connected ? "link.badge.plus" : "link")
                    }.help(store.connected ? "Отключить контроллеры" : "Подключить выбранные контроллеры")
                        .disabled(!store.ready || store.settings.Devices.isEmpty)
                    Toggle(isOn: $store.power) { Label("Питание", systemImage: "power") }
                        .toggleStyle(.button).help("Включить или выключить свет").disabled(!store.connected)
                }
            }
        }
        .onChange(of: store.settings) { _, _ in store.scheduleSave() }
        .onChange(of: store.page) { _, p in if let p, p != .devices { store.activeMode = p.rawValue; store.apply() } }
        .onChange(of: store.power) { _, on in store.send(["op":"power", "on":on]) }
        .sheet(isPresented: $adding) { DeviceEditor(store: store, original: nil) }
        .sheet(item: $editing) { device in DeviceEditor(store: store, original: device) }
        .alert("RGBroom", isPresented: Binding(get: { store.error != nil }, set: { if !$0 { store.error = nil } })) {
            Button("Повторить") { store.send(["op":"retry"]) }
            Button("OK", role: .cancel) { store.error = nil }
        } message: { Text(store.error ?? "") }
    }
    private var modePage: some View {
        Form {
            Section {
                HStack(spacing: 20) {
                    RoundedRectangle(cornerRadius: 18).fill(previewColor.gradient).frame(width: 76, height: 76)
                        .overlay(RoundedRectangle(cornerRadius: 18).stroke(.primary.opacity(0.08)))
                        .accessibilityLabel("Текущий цвет \(store.output.hex)")
                    VStack(alignment: .leading, spacing: 6) {
                        Text(store.connected ? (store.power ? "Свет включён" : "Свет выключен") : "Настройте свет").font(.title3.weight(.semibold))
                        Text(store.connected ? store.output.hex : "Подключите контроллеры, чтобы применить настройки").font(.callout).foregroundStyle(.secondary)
                    }
                    Spacer()
                }.padding(.vertical, 12)
            }
            switch store.page ?? .color {
            case .color:
                Section("Цвет") {
                    ColorPicker("Оттенок", selection: Binding(get: { store.settings.Color.color }, set: { store.settings.Color = RGB(color: $0) }), supportsOpacity: false)
                    HStack(spacing: 12) {
                        ForEach(Array([Color.red, .orange, .yellow, .green, .cyan, .blue, .purple, .pink, .white].enumerated()), id: \.offset) { i, color in
                            Button { store.settings.Color = RGB(color: color) } label: {
                                Circle().fill(color.gradient).frame(width: 26, height: 26).overlay(Circle().stroke(.primary.opacity(0.15)))
                            }.buttonStyle(.plain).accessibilityLabel(["Красный", "Оранжевый", "Жёлтый", "Зелёный", "Голубой", "Синий", "Фиолетовый", "Розовый", "Белый"][i])
                        }
                    }.padding(.vertical, 6)
                    SettingSlider(title: "Яркость", value: $store.settings.Brightness, range: 0...100, suffix: "%")
                }
            case .transition:
                Section("Плавный переход") {
                    SettingSlider(title: "Яркость", value: $store.settings.TransitionBrightness, range: 0...100, suffix: "%")
                    SettingSlider(title: "Полный цикл", value: $store.settings.TransitionSeconds, range: 3...60, suffix: " с")
                }
            case .music, .spectrum:
                AudioControls(profile: audioBinding, levels: store.levels)
            case .ambient:
                Section("Дисплей") {
                    Picker("Источник", selection: $store.settings.Display) {
                        Text("Основной дисплей").tag("")
                        ForEach(store.displays) { display in Text(display.Name).tag(display.Id) }
                    }
                    Button("Обновить дисплеи", systemImage: "arrow.clockwise") { store.send(["op":"displays"]) }
                    SettingSlider(title: "Яркость", value: $store.settings.AmbientBrightness, range: 0...100, suffix: "%")
                }
                Section { Text("RGBroom определяет основной цвет экрана и плавно меняет подсветку. Для этого разрешите запись экрана в Системных настройках. Изображение не сохраняется.").font(.callout).foregroundStyle(.secondary) }
            case .devices: EmptyView()
            }
            if !store.connected {
                Section { Button("Добавить или подключить контроллеры", systemImage: "lightbulb.2") { store.page = .devices } }
            }
        }.formStyle(.grouped)
    }
    private var previewColor: Color { store.connected ? (store.power ? store.output.color : .black) : store.settings.Color.color }
    private var audioBinding: Binding<AudioProfile> { store.page == .spectrum ? $store.settings.Spectrum : $store.settings.Music }
    private var devicesPage: some View {
        VStack(spacing: 0) {
            if store.settings.Devices.isEmpty {
                ContentUnavailableView {
                    Label("Добавьте свет в комнату", systemImage: "lightbulb.2")
                } description: { Text("Найдите контроллеры в локальной сети или по Bluetooth. Wi-Fi-контроллер можно добавить по IP-адресу.") }
                actions: { Button("Добавить контроллер", systemImage: "plus") { adding = true }.buttonStyle(.borderedProminent) }
            } else {
                List {
                    ForEach($store.settings.Devices) { $device in
                        HStack(spacing: 14) {
                            Toggle("Выбрать \(device.title)", isOn: $device.Selected).labelsHidden().toggleStyle(.checkbox)
                            Image(systemName: device.Protocol == 10 || device.Protocol == 11 ? "antenna.radiowaves.left.and.right" : "wifi").font(.title3).foregroundStyle(.tint).frame(width: 26)
                            VStack(alignment: .leading, spacing: 4) {
                                Text(device.title).font(.body.weight(.medium))
                                Text("\(device.family) · \(device.Ip)").font(.caption).foregroundStyle(.secondary).textSelection(.enabled)
                                if let status = store.statuses[device.Ip] { Text(status).font(.caption).foregroundStyle(.secondary) }
                            }
                            Spacer()
                            Button("Настроить", systemImage: "slider.horizontal.3") { editing = device }.labelStyle(.iconOnly).buttonStyle(.borderless)
                        }.padding(.vertical, 8)
                        .contextMenu {
                            Button("Настроить") { editing = device }
                            Button("Удалить", role: .destructive) { store.settings.Devices.removeAll { $0.id == device.id }; if store.connected { store.connect() } }
                        }
                    }
                }.listStyle(.inset)
            }
            Divider()
            HStack {
                Button("Добавить", systemImage: "plus") { adding = true }
                Menu {
                    Button("В локальной сети") { store.scan(ble: false) }
                    Button("Bluetooth LE") { store.scan(ble: true) }
                } label: { Label("Найти", systemImage: "magnifyingglass") }.disabled(store.scanning || !store.ready)
                if store.scanning { ProgressView().controlSize(.small); Text("Поиск…").font(.callout).foregroundStyle(.secondary) }
                Spacer()
                Text("\(store.settings.Devices.count) контроллеров").font(.callout).foregroundStyle(.secondary)
            }.padding(16)
        }
    }
}

struct SettingSlider: View {
    let title: String
    @Binding var value: Double
    let range: ClosedRange<Double>
    let suffix: String
    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack { Text(title); Spacer(); Text("\(Int(value.rounded()))\(suffix)").monospacedDigit().foregroundStyle(.secondary) }
            Slider(value: $value, in: range).accessibilityLabel(title).accessibilityValue("\(Int(value))\(suffix)")
        }.padding(.vertical, 5)
    }
}
struct AudioControls: View {
    @Binding var profile: AudioProfile
    let levels: [Double]
    var body: some View {
        Section("Реакция на музыку") {
            Picker("Характер", selection: $profile.Style) {
                Text("Плавный").tag(0); Text("Пульсация").tag(1); Text("Энергичный").tag(2); Text("Стробоскоп").tag(3)
            }
            if profile.Style == 3 {
                SettingSlider(title: "Яркость вспышки", value: $profile.FlashBrightness, range: 0...100, suffix: "%")
                SettingSlider(title: "Длительность вспышки", value: $profile.FlashMilliseconds, range: 30...160, suffix: " мс")
            } else { SettingSlider(title: "Яркость", value: $profile.Brightness, range: 0...100, suffix: "%") }
            SettingSlider(title: "Чувствительность", value: $profile.Gain, range: 1...30, suffix: "×")
        }
        Section("Частоты") {
            HStack(spacing: 16) {
                ForEach(0..<3) { i in VStack(alignment: .leading) { Text(["Низкие", "Средние", "Высокие"][i]).font(.caption).foregroundStyle(.secondary); ProgressView(value: levels.indices.contains(i) ? levels[i] : 0).tint([Color.red, .blue, .green][i]) } }
            }
            SettingSlider(title: "Низкие", value: $profile.Low, range: 0...200, suffix: "%")
            SettingSlider(title: "Средние", value: $profile.Mid, range: 0...200, suffix: "%")
            SettingSlider(title: "Высокие", value: $profile.High, range: 0...200, suffix: "%")
        }
        Section { Label("Системный звук · все приложения", systemImage: "speaker.wave.2"); Text("Нужно разрешение записи экрана и системного аудио. Звук обрабатывается на Mac и не сохраняется.").font(.callout).foregroundStyle(.secondary) }
    }
}

struct DeviceEditor: View {
    @ObservedObject var store: RoomStore
    let original: Device?
    @Environment(\.dismiss) private var dismiss
    @State private var draft = Device()
    @State private var key = ""
    @State private var validation = ""
    var body: some View {
        VStack(spacing: 0) {
            HStack { Text(original == nil ? "Новый контроллер" : "Настройки контроллера").font(.headline); Spacer() }.padding(20)
            Form {
                TextField("Название", text: $draft.Name)
                TextField(draft.Protocol == 10 || draft.Protocol == 11 ? "UUID Bluetooth" : "IP-адрес", text: $draft.Ip).disabled(original != nil)
                Picker("Протокол", selection: $draft.Protocol) { ForEach(protocols, id: \.0) { item in Text(item.1).tag(item.0) } }
                if draft.Protocol == 10 || draft.Protocol == 11 {
                    Text("Используйте поиск Bluetooth для получения UUID. MAC-адрес Windows на macOS не подходит.").font(.caption).foregroundStyle(.secondary)
                    if draft.Protocol == 10 { Toggle("Альтернативные пакеты ELK (handle 13)", isOn: $draft.BluetoothElk13).help("Включите, если контроллер подключается, но не реагирует на обычные команды ELK.") }
                }
                if draft.Protocol == 12 {
                    Section("Tuya LAN") {
                        TextField("Device ID", text: $draft.DeviceId)
                        SecureField(draft.ProtectedKey.isEmpty ? "local_key" : "Новый local_key (необязательно)", text: $key)
                        Picker("Версия", selection: $draft.TuyaVersion) { ForEach(["3.3","3.4","3.5"], id: \.self) { Text($0) } }
                        TextField("DP питания · 0 = авто", value: $draft.PowerDp, format: .number)
                        TextField("DP режима · 0 = авто", value: $draft.ModeDp, format: .number)
                        TextField("DP цвета · 0 = авто", value: $draft.ColorDp, format: .number)
                        Toggle("CONTROL_NEW (device22)", isOn: $draft.TuyaDevice22)
                        Text("Ключ хранится в Связке ключей macOS.").font(.caption).foregroundStyle(.secondary)
                    }
                }
                if !validation.isEmpty { Text(validation).foregroundStyle(.red) }
            }.formStyle(.grouped)
            HStack { Spacer(); Button("Отмена", role: .cancel) { dismiss() }.keyboardShortcut(.cancelAction); Button("Сохранить") { save() }.keyboardShortcut(.defaultAction).buttonStyle(.borderedProminent) }.padding(20)
        }.frame(width: 500, height: draft.Protocol == 12 ? 640 : 330)
            .onAppear { draft = original ?? Device() }
    }
    private func save() {
        draft.Ip = draft.Ip.trimmingCharacters(in: .whitespacesAndNewlines)
        let ble = draft.Protocol == 10 || draft.Protocol == 11
        let parts = draft.Ip.split(separator: ".", omittingEmptySubsequences: false)
        let validIP = parts.count == 4 && parts.allSatisfy { !$0.isEmpty && $0.allSatisfy(\.isNumber) && UInt8($0) != nil }
        guard ble ? UUID(uuidString: draft.Ip) != nil : validIP else { validation = ble ? "Введите UUID из поиска Bluetooth." : "Введите IPv4-адрес, например 192.168.1.50."; return }
        guard original != nil || !store.settings.Devices.contains(where: { $0.Ip == draft.Ip }) else { validation = "Этот контроллер уже добавлен."; return }
        if draft.Protocol == 12 {
            guard !draft.DeviceId.isEmpty, key.utf8.count == 16 || key.isEmpty && !draft.ProtectedKey.isEmpty else { validation = "Введите Device ID и local_key длиной 16 байт."; return }
            guard [draft.PowerDp,draft.ModeDp,draft.ColorDp].allSatisfy({ (0...255).contains($0) }) else { validation = "DP должны быть в диапазоне 0–255."; return }
        }
        if let index = store.settings.Devices.firstIndex(where: { $0.id == original?.id }) { store.settings.Devices[index] = draft } else { store.settings.Devices.append(draft) }
        store.apply()
        if !key.isEmpty { store.saveKey(key, device: draft) }
        if store.connected { store.connect() }
        dismiss()
    }
}
struct PreferencesView: View {
    var body: some View {
        Form {
            Section("Конфиденциальность") {
                Text("Звук и изображение обрабатываются локально. Ключи Tuya хранятся в Связке ключей macOS.")
                Button("Разрешения записи экрана") { NSWorkspace.shared.open(URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_ScreenCapture")!) }
                Button("Разрешения Bluetooth") { NSWorkspace.shared.open(URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_Bluetooth")!) }
            }
        }.formStyle(.grouped)
    }
}
