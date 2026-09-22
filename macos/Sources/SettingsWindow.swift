import AppKit

private final class SettingsDocumentView: NSView {
    override var isFlipped: Bool { true }
}

final class SettingsWindow: NSWindowController, NSTableViewDataSource, NSTableViewDelegate, NSTextFieldDelegate {
    let store: SettingsStore
    let sprites: SpriteCache
    var onChanged: (() -> Void)?
    var onModelChanged: (() -> Void)?
    var onInstall: (() -> Void)?
    var onPermissions: (() -> Void)?
    var onPreview: ((String) -> Void)?
    var onTest: ((String) -> Void)?
    var onTap: (() -> Void)?
    var onSnap: (() -> Void)?
    private let table = NSTableView()
    private let nameField = NSTextField()
    private let descriptionField = NSTextField()
    private let fileLabel = NSTextField(wrappingLabelWithString: "")
    private let preview = NSImageView()
    private let pose = NSPopUpButton()
    private let poses = ["idle", "left", "right", "both"]
    private let status = NSTextField(wrappingLabelWithString: "Laya를 준비하고 있습니다…")
    private let inputStatus = NSTextField(wrappingLabelWithString: "")
    private let testInput = NSTextField()
    private let testResult = NSTextField(wrappingLabelWithString: "")
    private let model = NSPopUpButton()
    private let promptField = NSTextField()
    private let capture = NSButton(checkboxWithTitle: "입력으로 감정 바꾸기", target: nil, action: nil)
    private let size = NSSlider(value: 320, minValue: 180, maxValue: 700, target: nil, action: nil)
    private let sizeLabel = NSTextField(labelWithString: "320 px")
    private let installButton = NSButton(title: "모델 설치 / 복구", target: nil, action: nil)
    private var loadedRow = -1
    private var isLoading = false

    init(store: SettingsStore, sprites: SpriteCache) {
        self.store = store
        self.sprites = sprites
        let window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 840, height: 700),
                              styleMask: [.titled, .closable, .miniaturizable, .resizable], backing: .buffered, defer: false)
        window.title = "EmotionCat 설정"
        window.minSize = NSSize(width: 800, height: 680)
        window.isReleasedWhenClosed = false
        super.init(window: window)
        build()
        window.center()
    }
    required init?(coder: NSCoder) { fatalError("init(coder:) is unavailable") }

    private func button(_ title: String, _ action: Selector) -> NSButton {
        let result = NSButton(title: title, target: self, action: action)
        result.bezelStyle = .rounded
        return result
    }
    private func row(_ views: [NSView], spacing: CGFloat = 8) -> NSStackView {
        let result = NSStackView(views: views)
        result.orientation = .horizontal
        result.alignment = .centerY
        result.spacing = spacing
        return result
    }
    private func build() {
        guard let content = window?.contentView else { return }
        let title = NSTextField(labelWithString: "EmotionCat")
        title.font = .systemFont(ofSize: 20, weight: .semibold)
        model.addItems(withTitles: ["Laya Multilingual 322M · 한국어 / 영어"])
        model.selectItem(at: 0)
        model.isEnabled = false
        model.target = self; model.action = #selector(changeModel)
        installButton.target = self; installButton.action = #selector(installModel)
        installButton.bezelStyle = .rounded
        let modelRow = row([NSTextField(labelWithString: "모델"), model, installButton, button("다시 연결", #selector(restartModel))])
        status.font = .systemFont(ofSize: 12)
        status.textColor = .secondaryLabelColor
        status.maximumNumberOfLines = 3
        capture.state = store.settings.captureText ? .on : .off
        capture.target = self; capture.action = #selector(changeCapture)
        let permissionRow = row([capture, button("입력 권한 설정", #selector(permissions))])
        inputStatus.font = .systemFont(ofSize: 11)
        inputStatus.textColor = .secondaryLabelColor
        inputStatus.maximumNumberOfLines = 2
        size.doubleValue = store.settings.size
        size.target = self; size.action = #selector(changeSize)
        size.isContinuous = true
        size.widthAnchor.constraint(greaterThanOrEqualToConstant: 160).isActive = true
        sizeLabel.stringValue = "\(Int(store.settings.size)) px"
        let appearanceRow = row([NSTextField(labelWithString: "크기"), size, sizeLabel,
                                 button("Dock 위에 맞추기", #selector(snap)), button("양발 테스트", #selector(tap))])

        let column = NSTableColumn(identifier: NSUserInterfaceItemIdentifier("emotion"))
        column.title = "감정 · 지정된 이미지"
        column.width = 285
        table.addTableColumn(column)
        table.headerView = nil
        table.rowHeight = 59
        table.intercellSpacing = NSSize(width: 0, height: 3)
        table.dataSource = self; table.delegate = self
        table.allowsEmptySelection = false
        table.columnAutoresizingStyle = .firstColumnOnlyAutoresizingStyle
        let scroll = NSScrollView()
        scroll.documentView = table
        scroll.hasVerticalScroller = true
        scroll.borderType = .bezelBorder
        scroll.heightAnchor.constraint(greaterThanOrEqualToConstant: 244).isActive = true
        scroll.widthAnchor.constraint(equalToConstant: 300).isActive = true
        let listColumn = NSStackView(views: [scroll, row([button("감정 추가", #selector(addEmotion)), button("삭제", #selector(removeEmotion))])])
        listColumn.orientation = .vertical; listColumn.alignment = .leading; listColumn.spacing = 8

        preview.imageScaling = .scaleProportionallyUpOrDown
        preview.heightAnchor.constraint(equalToConstant: 146).isActive = true
        preview.widthAnchor.constraint(equalToConstant: 220).isActive = true
        pose.addItems(withTitles: ["기본 자세", "왼발 들기", "오른발 들기", "양발 들기"])
        pose.target = self; pose.action = #selector(changePose)
        nameField.placeholderString = "감정 이름"
        nameField.delegate = self
        descriptionField.placeholderString = "분류 기준"
        descriptionField.delegate = self
        descriptionField.cell?.wraps = true
        descriptionField.cell?.isScrollable = false
        descriptionField.heightAnchor.constraint(equalToConstant: 49).isActive = true
        fileLabel.font = .systemFont(ofSize: 11)
        fileLabel.textColor = .secondaryLabelColor
        fileLabel.maximumNumberOfLines = 2
        let editor = NSStackView(views: [preview, row([NSTextField(labelWithString: "동작"), pose]), nameField, descriptionField, fileLabel,
            row([button("이미지 선택…", #selector(chooseImage)), button("기본 이미지", #selector(resetImage)), button("표정 보기", #selector(showEmotion))])])
        editor.orientation = .vertical; editor.alignment = .leading; editor.spacing = 8
        nameField.widthAnchor.constraint(equalTo: editor.widthAnchor).isActive = true
        descriptionField.widthAnchor.constraint(equalTo: editor.widthAnchor).isActive = true
        fileLabel.widthAnchor.constraint(equalTo: editor.widthAnchor).isActive = true
        let mappingRow = row([listColumn, editor], spacing: 18)
        mappingRow.alignment = .top
        let mappingTitle = NSTextField(labelWithString: "감정 → 이미지")
        mappingTitle.font = .systemFont(ofSize: 14, weight: .semibold)
        testInput.placeholderString = "예: 너무 좋아! 오늘 정말 행복해"
        let testRow = row([testInput, button("Laya 감정 테스트", #selector(test))])
        testInput.setContentHuggingPriority(.defaultLow, for: .horizontal)
        testResult.font = .systemFont(ofSize: 12)
        testResult.textColor = .secondaryLabelColor
        promptField.stringValue = store.settings.classificationPrompt
        let promptRow = row([NSTextField(labelWithString: "분류 지시문"), promptField, button("저장", #selector(savePrompt))])
        promptField.setContentHuggingPriority(.defaultLow, for: .horizontal)
        let root = NSStackView(views: [title, modelRow, status, permissionRow, inputStatus, appearanceRow, mappingTitle, mappingRow, testRow, testResult, promptRow])
        root.orientation = .vertical
        root.alignment = .leading
        root.spacing = 11
        root.translatesAutoresizingMaskIntoConstraints = false
        let page = NSScrollView(frame: content.bounds)
        page.autoresizingMask = [.width, .height]
        page.hasVerticalScroller = true
        page.drawsBackground = false
        let document = SettingsDocumentView()
        document.translatesAutoresizingMaskIntoConstraints = false
        page.documentView = document
        content.addSubview(page)
        document.addSubview(root)
        NSLayoutConstraint.activate([
            document.leadingAnchor.constraint(equalTo: page.contentView.leadingAnchor),
            document.topAnchor.constraint(equalTo: page.contentView.topAnchor),
            document.widthAnchor.constraint(equalTo: page.contentView.widthAnchor),
            root.leadingAnchor.constraint(equalTo: document.leadingAnchor, constant: 22),
            root.trailingAnchor.constraint(equalTo: document.trailingAnchor, constant: -22),
            root.topAnchor.constraint(equalTo: document.topAnchor, constant: 20),
            root.bottomAnchor.constraint(equalTo: document.bottomAnchor, constant: -18),
            status.widthAnchor.constraint(equalTo: root.widthAnchor),
            inputStatus.widthAnchor.constraint(equalTo: root.widthAnchor),
            mappingRow.widthAnchor.constraint(equalTo: root.widthAnchor),
            appearanceRow.widthAnchor.constraint(equalTo: root.widthAnchor),
            testRow.widthAnchor.constraint(equalTo: root.widthAnchor),
            testResult.widthAnchor.constraint(equalTo: root.widthAnchor)
        ])
        table.reloadData()
        table.selectRowIndexes(IndexSet(integer: 0), byExtendingSelection: false)
        loadSelection()
    }

    func open() {
        showWindow(nil)
        NSApp.activate(ignoringOtherApps: true)
        window?.makeKeyAndOrderFront(nil)
    }
    func setStatus(_ value: String) { status.stringValue = value }
    func setInputStatus(_ value: String) { inputStatus.stringValue = value }
    func setTestResult(_ value: String) { testResult.stringValue = value }
    func setInstalling(_ value: Bool) {
        installButton.isEnabled = !value
        model.isEnabled = false
        installButton.title = value ? "설치 중…" : "모델 설치 / 복구"
    }

    func numberOfRows(in tableView: NSTableView) -> Int { store.settings.emotions.count }
    func tableView(_ tableView: NSTableView, viewFor tableColumn: NSTableColumn?, row: Int) -> NSView? {
        let emotion = store.settings.emotions[row]
        let image = NSImageView()
        image.image = sprites.image(emotion.imagePath)
        image.imageScaling = .scaleProportionallyUpOrDown
        image.widthAnchor.constraint(equalToConstant: 74).isActive = true
        image.heightAnchor.constraint(equalToConstant: 54).isActive = true
        let name = NSTextField(labelWithString: emotion.name)
        name.font = .systemFont(ofSize: 13, weight: .medium)
        let path = NSTextField(labelWithString: URL(fileURLWithPath: emotion.imagePath).lastPathComponent)
        path.textColor = image.image == nil ? .systemRed : .secondaryLabelColor
        path.font = .systemFont(ofSize: 10)
        path.lineBreakMode = .byTruncatingMiddle
        let labels = NSStackView(views: [name, path])
        labels.orientation = .vertical; labels.alignment = .leading; labels.spacing = 3
        return self.row([image, labels])
    }
    func tableViewSelectionDidChange(_ notification: Notification) { commitEditor(); loadSelection() }
    func controlTextDidEndEditing(_ obj: Notification) { commitEditor() }

    private func loadSelection() {
        guard store.settings.emotions.indices.contains(table.selectedRow) else { return }
        isLoading = true
        loadedRow = table.selectedRow
        let item = store.settings.emotions[loadedRow]
        nameField.stringValue = item.name
        descriptionField.stringValue = item.description
        let imagePath = item.path(for: poses[max(0, pose.indexOfSelectedItem)])
        fileLabel.stringValue = imagePath
        preview.image = sprites.image(imagePath)
        isLoading = false
    }
    private func commitEditor() {
        guard !isLoading, store.settings.emotions.indices.contains(loadedRow) else { return }
        let name = String(nameField.stringValue.trimmingCharacters(in: .whitespacesAndNewlines).prefix(80))
        let description = String(descriptionField.stringValue.prefix(400))
        guard store.settings.emotions[loadedRow].name != name || store.settings.emotions[loadedRow].description != description else { return }
        store.settings.emotions[loadedRow].name = name.isEmpty ? store.settings.emotions[loadedRow].id : name
        store.settings.emotions[loadedRow].description = description
        table.reloadData(forRowIndexes: IndexSet(integer: loadedRow), columnIndexes: IndexSet(integer: 0))
        changed()
    }
    private func changed() {
        do { try store.save(); onChanged?() }
        catch { alert(error.localizedDescription) }
    }
    private func alert(_ message: String) {
        let alert = NSAlert()
        alert.messageText = "EmotionCat"
        alert.informativeText = message
        if let window = window { alert.beginSheetModal(for: window) }
    }
    @objc private func chooseImage() {
        guard store.settings.emotions.indices.contains(loadedRow), let window = window else { return }
        commitEditor()
        let target = loadedRow
        let targetPose = poses[max(0, pose.indexOfSelectedItem)]
        let panel = NSOpenPanel()
        panel.allowedFileTypes = ["png", "jpg", "jpeg", "webp", "tiff"]
        panel.canChooseDirectories = false; panel.allowsMultipleSelection = false
        panel.beginSheetModal(for: window) { [weak self] response in
            guard let self = self, response == .OK, let url = panel.url,
                  self.store.settings.emotions.indices.contains(target) else { return }
            do {
                self.store.settings.emotions[target].setPath(try self.store.importImage(url), for: targetPose)
                self.sprites.clear()
                self.table.reloadData()
                self.loadSelection()
                self.changed()
            } catch { self.alert(error.localizedDescription) }
        }
    }
    @objc private func resetImage() {
        guard store.settings.emotions.indices.contains(loadedRow) else { return }
        let id = store.settings.emotions[loadedRow].id
        let selectedPose = poses[max(0, pose.indexOfSelectedItem)]
        let original = EmotionDefinition.defaults.first(where: { $0.id == id }) ?? EmotionDefinition.defaults[0]
        store.settings.emotions[loadedRow].setPath(original.path(for: selectedPose), for: selectedPose)
        table.reloadData(); loadSelection(); changed()
    }
    @objc private func addEmotion() {
        guard store.settings.emotions.count < 16 else { alert("감정은 최대 16개까지 설정할 수 있습니다."); return }
        commitEditor()
        var definition = EmotionDefinition.defaults[0]
        definition.id = "custom_" + String(UUID().uuidString.prefix(12))
        definition.name = "새 감정"
        definition.description = "Describe this emotion."
        store.settings.emotions.append(definition)
        table.reloadData()
        table.selectRowIndexes(IndexSet(integer: store.settings.emotions.count - 1), byExtendingSelection: false)
        loadSelection(); changed()
    }
    @objc private func removeEmotion() {
        guard store.settings.emotions.indices.contains(loadedRow) else { return }
        guard store.settings.emotions.count > 2, store.settings.emotions[loadedRow].id != "neutral" else {
            alert("평온은 기본 표정입니다. 감정은 최소 2개가 필요합니다."); return
        }
        store.settings.emotions.remove(at: loadedRow)
        loadedRow = -1
        table.reloadData()
        table.selectRowIndexes(IndexSet(integer: 0), byExtendingSelection: false)
        loadSelection(); changed()
    }
    @objc private func showEmotion() {
        commitEditor()
        guard store.settings.emotions.indices.contains(loadedRow) else { return }
        onPreview?(store.settings.emotions[loadedRow].id)
    }
    @objc private func test() {
        commitEditor()
        let text = String(testInput.stringValue.suffix(600))
        guard !text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else { return }
        testResult.stringValue = "Laya가 감정을 고르고 있습니다…"
        onTest?(text)
    }
    @objc private func changeModel() {
        store.settings.model = "multilingual"
        changed(); onModelChanged?()
    }
    @objc private func savePrompt() {
        let value = promptField.stringValue.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !value.isEmpty else { return }
        store.settings.classificationPrompt = String(value.prefix(1000))
        changed()
    }
    @objc private func restartModel() { onModelChanged?() }
    @objc private func installModel() { commitEditor(); onInstall?() }
    @objc private func changeCapture() { store.settings.captureText = capture.state == .on; changed() }
    @objc private func changeSize() {
        store.settings.size = size.doubleValue.rounded()
        sizeLabel.stringValue = "\(Int(store.settings.size)) px"
        changed()
    }
    @objc private func permissions() { onPermissions?() }
    @objc private func changePose() { commitEditor(); loadSelection() }
    @objc private func tap() { onTap?() }
    @objc private func snap() { onSnap?() }
}
