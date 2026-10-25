# RGBroom: совместимость и найденные контроллеры

Список описывает реализованные семейства протоколов. Новые драйверы проверены на локальных эмуляторах и тестовых пакетах, а не на физических контроллерах. Название приложения на коробке не заменяет проверку конкретной аппаратной ревизии.

| Семейство | Что поддерживает RGBroom | Что требуется |
|---|---|---|
| Magic Home / LEDENET | Старые RGB/RGBW, RGB8/RGB9, SPI2/SPI3, новые HSV-команды | IP; Auto или явный вариант протокола |
| Tuya / Smart Life Wi-Fi | Прямой LAN 3.3, 3.4, 3.5; AES, проверка целостности, согласование сессионного ключа; RGB DP 20/21/24 и 1/2/5, ручные DP и device22 | IP, Device ID, действующий 16-байтовый local_key и версия протокола |
| WLED | HTTP JSON API: цвет основного сегмента, питание, яркость, проверка состояния | IP и доступный HTTP-порт 80; добавление вручную |
| Yeelight LAN | RGB, питание, проверка состояния; обратное music TCP-соединение для частой смены цвета | IP, включённый LAN Control и поддержка set_music; Windows должна разрешать входящее соединение из локальной сети |
| Lotus Lantern / ELK-BLEDOM | BLE FFF0 → FFF3; варианты команд для характерных handle 9/13; общий RGB, питание и яркость | BLE-адаптер, поддерживаемый контроллер и свободное соединение с ним |
| Triones / LED BLE | BLE FFD5 → FFD9; RGBW-пакеты с управлением RGB-каналами, питание | BLE-адаптер и контроллер с этим GATT-профилем |

Общая группа может содержать разные Wi-Fi/BLE семейства. Проверка связи и переподключение выполняются отдельно для каждого устройства. BLE и Tuya обновляют цвет медленнее, чем типичные Magic Home/WLED; фактическая плавность и синхронность зависят от контроллера.

## Реальные примеры из открытых источников

### MiBoxer WL5 — кандидат для Tuya / Smart Life

В [каталоге производителя MiBoxer 2024–2025](https://miboxer.com/light/download/2024_2025_catalogue.pdf) для WL5 указан Tuya app. [Документация устройства в проекте ESPHome](https://devices.esphome.io/devices/miboxer-wl5-led-controller/) описывает ревизию WL5 с модулем Tuya WR3.

Это пример аппаратного семейства для дальнейшей проверки, а не гарантия работы любой коробки с маркировкой WL5. У WL5 встречаются разные поколения. Перед покупкой нужна именно Tuya/Smart Life Wi-Fi ревизия; у продавца следует уточнить версию LAN-протокола, доступность Device ID/local_key и RGB data points. RGBroom не требует перепрошивки, но и не получает ключ из облачной учётной записи автоматически.

### ELK-BLEDOM — распространённый безымянный BLE-контроллер

В [первичном описании протокола ELK-BLEDOM](https://github.com/FergusInLondon/ELK-BLEDOM/blob/master/PROTCOL.md) приведены GATT-характеристика FFF3 и команды RGB. Такие контроллеры выпускаются разными продавцами; торговое название само по себе малоинформативно. Для RGBroom важны рекламируемое BLE-имя и сервис/характеристика, а не длина подключённой ленты.

### ELK-BLEDDM — пример, проверенный с Lotus Lantern другим разработчиком

Автор [Lotus Lantern BLE Music Sync for Windows](https://github.com/razibit/Lotus-Lantern-for-Windows-PC) сообщает о проверке контроллера ELK-BLEDDM с Lotus Lantern и указывает профиль FFF0/FFF3 и команды семейства 7E 07 05 03. Это документированное совпадение семейства, но физической проверки этого устройства именно с RGBroom не проводилось.

### duoCo Strip: приложение не задаёт один аппаратный SKU

В [инструкции Bluetooth-подсветки, размещённой Home Depot](https://images.thdstatic.com/catalog/pdfImages/22/22cacec4-298c-47ca-8925-98ecef4c1e88.pdf) указано приложение duoCo Strip. Надёжной единственной модели контроллера для всего приложения найти не удалось.

Для покупки варианта под RGBroom нужно получить у продавца точное BLE-имя и подтверждение профиля ELK FFF0/FFF3 или Triones FFD5/FFD9. Просто надписи «duoCo Strip compatible» недостаточно. Не заявляется поддержка всех устройств duoCo Strip.

## Существенные границы поддержки

- Tuya Zigbee через шлюз, Tuya BLE Mesh, облачные Tuya API и произвольные OEM-схемы DP не поддерживаются как универсальный режим. Прямой Tuya LAN требует ключ вашего устройства; RGBroom не извлекает чужие ключи.
- ELK-BLEDOB / LotusLamp X — другой протокол, не следует выбирать его как ELK-BLEDOM. Его различия описаны [в отдельном проекте разработчика](https://github.com/8none1/elk-bledob).
- Названия Lotus Lantern, LotusLamp X, duoCo Strip, duoCo StripX и LED BLE нельзя считать взаимозаменяемыми.
- Для Tuya поддерживаются обычные цветовые DP. Произвольная адресная разметка сегментов и встроенные фирменные эффекты не переносятся автоматически.
- У Yeelight нужна прошивка с LAN Control и music mode. RGBroom не включает скрытые функции прошивки и не обходит их отсутствие.

## Технические источники

- [Tuya Local Protocol Reference](https://github.com/jasonacox/tinytuya/blob/master/PROTOCOL.md)
- [WLED JSON API](https://kno.wled.ge/interfaces/json-api/)
- [Официальная спецификация Yeelight LAN](https://www.yeelight.com/download/Yeelight_Inter-Operation_Spec.pdf)
- [GATT-протокол Triones](https://github.com/madhead/saberlight/blob/master/protocols/Triones/protocol.md)
- [Windows Bluetooth GATT Client](https://learn.microsoft.com/en-us/windows/apps/develop/devices-sensors/gatt-client)


## Подсветка ПК — OpenRGB (9.1.0)

Asus Aura Sync, MSI Mystic Light, Gigabyte RGB Fusion и ASRock Polychrome представлены через устройства, которые обнаруживает OpenRGB. Поддержка конкретной модели зависит от версии OpenRGB и наличия Direct-режима. Прямые фирменные SDK не используются.

Подключение: TCP SDK Server, по умолчанию 127.0.0.1:6742. Клиент согласует SDK 3 (совместим с более новыми серверами), поддерживает SDK 1–3. Старый неверсионированный SDK 0 не поддерживается. Можно выбирать целые компоненты либо отдельные зоны; размеры ARGB-зон настраиваются в OpenRGB. Режимы, прошивка и размеры LED аппаратуры не сохраняются и не изменяются.

Программно проверены обнаружение, бинарные данные, Direct, RGB и зоны, чёрный цвет при выключении, heartbeat, изменение списка и повторное подключение. Проверка на реальных компонентах пока не выполнена.

Источники: [SDK](https://openrgb.org/sdk.html), [протокол](https://github.com/CalcProgrammer1/OpenRGB/blob/master/Documentation/OpenRGBSDK.md), [оборудование](https://openrgb.org/devices.html).
