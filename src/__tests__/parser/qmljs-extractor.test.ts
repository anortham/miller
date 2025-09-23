import { describe, it, expect, beforeAll } from 'bun:test';
import { ParserManager } from '../../parser/parser-manager.js';
import { QMLJSExtractor } from '../../extractors/qmljs-extractor.js';
import { SymbolKind } from '../../extractors/base-extractor.js';

describe('QMLJSExtractor', () => {
  let parserManager: ParserManager;

  beforeAll(async () => {
    // Initialize logger for tests
    const { initializeLogger } = await import('../../utils/logger.js');
    const { MillerPaths } = await import('../../utils/miller-paths.js');
    const paths = new MillerPaths(process.cwd());
    await paths.ensureDirectories();
    initializeLogger(paths);

    parserManager = new ParserManager();
    await parserManager.initialize();
  });

  describe('QML Components and Properties', () => {
    it('should extract QML components, properties, and basic structure', async () => {
      const qmlCode = `
import QtQuick 2.15
import QtQuick.Controls 2.15

Rectangle {
    id: mainWindow
    width: 800
    height: 600
    color: "#f0f0f0"

    property string title: "QML Application"
    property int currentPage: 0
    property real scaleFactor: 1.0
    property bool isVisible: true

    readonly property string version: "1.0.0"

    signal pageChanged(int newPage)
    signal userClicked(string buttonName)

    Item {
        id: contentArea
        anchors.fill: parent
        anchors.margins: 20

        Text {
            id: titleText
            text: parent.title || "Default Title"
            font.pointSize: 24
            anchors.centerIn: parent
            color: "#333333"
        }

        Button {
            id: actionButton
            text: "Click Me"
            anchors.bottom: parent.bottom
            anchors.horizontalCenter: parent.horizontalCenter

            onClicked: {
                console.log("Button clicked");
                parent.userClicked("actionButton");
            }
        }
    }
}
      `;

      const parseResult = await parserManager.parseFile('test.qml', qmlCode);
      const extractor = new QMLJSExtractor('qmljs', 'test.qml', qmlCode);
      const symbols = extractor.extractSymbols(parseResult.tree);

      expect(symbols.length).toBeGreaterThanOrEqual(8);

      // Check main Rectangle component
      const mainWindow = symbols.find(s => s.name === 'mainWindow' && s.kind === SymbolKind.Class);
      expect(mainWindow).toBeDefined();
      expect(mainWindow?.signature).toContain('Rectangle');

      // Check properties
      const titleProp = symbols.find(s => s.name === 'title' && s.kind === SymbolKind.Property);
      expect(titleProp).toBeDefined();

      const currentPageProp = symbols.find(s => s.name === 'currentPage' && s.kind === SymbolKind.Property);
      expect(currentPageProp).toBeDefined();

      // Check readonly property
      const versionProp = symbols.find(s => s.name === 'version' && s.kind === SymbolKind.Property);
      expect(versionProp).toBeDefined();

      // Check nested components
      const contentArea = symbols.find(s => s.name === 'contentArea' && s.kind === SymbolKind.Class);
      expect(contentArea).toBeDefined();

      const titleText = symbols.find(s => s.name === 'titleText' && s.kind === SymbolKind.Class);
      expect(titleText).toBeDefined();

      const actionButton = symbols.find(s => s.name === 'actionButton' && s.kind === SymbolKind.Class);
      expect(actionButton).toBeDefined();
    });
  });

  describe('JavaScript Functions and Logic', () => {
    it('should extract JavaScript functions within QML context', async () => {
      const qmlCode = `
import QtQuick 2.15

Item {
    id: root

    property int counter: 0

    function incrementCounter() {
        counter++;
        console.log("Counter:", counter);
    }

    function resetCounter() {
        counter = 0;
        return true;
    }

    function calculateSum(a, b) {
        return a + b;
    }

    function complexOperation(data) {
        if (!data || !data.length) {
            return null;
        }

        let result = data.reduce((sum, item) => {
            return sum + (item.value || 0);
        }, 0);

        return {
            total: result,
            average: result / data.length,
            timestamp: Date.now()
        };
    }

    Timer {
        id: updateTimer
        interval: 1000
        running: true
        repeat: true

        onTriggered: {
            root.incrementCounter();
            if (root.counter > 10) {
                stop();
            }
        }
    }
}
      `;

      const parseResult = await parserManager.parseFile('test.qml', qmlCode);
      const extractor = new QMLJSExtractor('qmljs', 'test.qml', qmlCode);
      const symbols = extractor.extractSymbols(parseResult.tree);

      expect(symbols.length).toBeGreaterThanOrEqual(6);

      // Check JavaScript functions
      const incrementCounter = symbols.find(s => s.name === 'incrementCounter' && s.kind === SymbolKind.Function);
      expect(incrementCounter).toBeDefined();
      expect(incrementCounter?.signature).toContain('incrementCounter()');

      const resetCounter = symbols.find(s => s.name === 'resetCounter' && s.kind === SymbolKind.Function);
      expect(resetCounter).toBeDefined();

      const calculateSum = symbols.find(s => s.name === 'calculateSum' && s.kind === SymbolKind.Function);
      expect(calculateSum).toBeDefined();
      expect(calculateSum?.signature).toContain('calculateSum(a, b)');

      const complexOperation = symbols.find(s => s.name === 'complexOperation' && s.kind === SymbolKind.Function);
      expect(complexOperation).toBeDefined();

      // Check QML component with JS logic
      const updateTimer = symbols.find(s => s.name === 'updateTimer' && s.kind === SymbolKind.Class);
      expect(updateTimer).toBeDefined();
    });
  });

  describe('Signals and Signal Handlers', () => {
    it('should extract signals, signal handlers, and connections', async () => {
      const qmlCode = `
import QtQuick 2.15

Rectangle {
    id: signalTest

    signal dataReady(var data)
    signal errorOccurred(string message, int code)
    signal simpleSignal()

    property string statusMessage: ""

    function handleDataReady(data) {
        console.log("Data received:", JSON.stringify(data));
        statusMessage = "Data loaded successfully";
    }

    function handleError(message, code) {
        console.error("Error occurred:", message, "Code:", code);
        statusMessage = "Error: " + message;
    }

    Component.onCompleted: {
        dataReady.connect(handleDataReady);
        errorOccurred.connect(handleError);
    }

    MouseArea {
        id: clickArea
        anchors.fill: parent

        onClicked: function(mouse) {
            if (mouse.button === Qt.LeftButton) {
                parent.simpleSignal();
            } else {
                parent.errorOccurred("Invalid click", 400);
            }
        }

        onPressed: {
            console.log("Mouse pressed at:", mouse.x, mouse.y);
        }

        onDoubleClicked: {
            let testData = {
                timestamp: Date.now(),
                position: { x: mouse.x, y: mouse.y },
                type: "double-click"
            };
            parent.dataReady(testData);
        }
    }
}
      `;

      const parseResult = await parserManager.parseFile('test.qml', qmlCode);
      const extractor = new QMLJSExtractor('qmljs', 'test.qml', qmlCode);
      const symbols = extractor.extractSymbols(parseResult.tree);

      expect(symbols.length).toBeGreaterThanOrEqual(6);

      // Check signals
      const dataReady = symbols.find(s => s.name === 'dataReady' && s.kind === SymbolKind.Function);
      expect(dataReady).toBeDefined();

      const errorOccurred = symbols.find(s => s.name === 'errorOccurred' && s.kind === SymbolKind.Function);
      expect(errorOccurred).toBeDefined();

      const simpleSignal = symbols.find(s => s.name === 'simpleSignal' && s.kind === SymbolKind.Function);
      expect(simpleSignal).toBeDefined();

      // Check signal handler functions
      const handleDataReady = symbols.find(s => s.name === 'handleDataReady' && s.kind === SymbolKind.Function);
      expect(handleDataReady).toBeDefined();

      const handleError = symbols.find(s => s.name === 'handleError' && s.kind === SymbolKind.Function);
      expect(handleError).toBeDefined();

      // Check MouseArea component
      const clickArea = symbols.find(s => s.name === 'clickArea' && s.kind === SymbolKind.Class);
      expect(clickArea).toBeDefined();
    });
  });

  describe('QML Imports and Modules', () => {
    it('should handle QML imports and module declarations', async () => {
      const qmlCode = `
import QtQuick 2.15
import QtQuick.Controls 2.15
import QtQuick.Layouts 1.15
import QtGraphicalEffects 1.15
import "components" as CustomComponents
import "../utils/DataManager.js" as DataManager

ApplicationWindow {
    id: app
    title: "Multi-Module Application"
    visible: true

    property alias mainLayout: layout

    ColumnLayout {
        id: layout
        anchors.fill: parent
        spacing: 10

        CustomComponents.HeaderBar {
            id: header
            Layout.fillWidth: true
            title: app.title
        }

        ScrollView {
            id: scrollArea
            Layout.fillWidth: true
            Layout.fillHeight: true

            ListView {
                id: dataList
                model: ListModel {
                    id: dataModel
                }

                delegate: CustomComponents.DataItem {
                    width: dataList.width
                    data: model

                    onItemClicked: {
                        DataManager.processItem(model.id);
                    }
                }
            }
        }

        CustomComponents.StatusBar {
            id: statusBar
            Layout.fillWidth: true
        }
    }

    Component.onCompleted: {
        DataManager.loadData().then(function(data) {
            for (let item of data) {
                dataModel.append(item);
            }
        });
    }
}
      `;

      const parseResult = await parserManager.parseFile('MainWindow.qml', qmlCode);
      const extractor = new QMLJSExtractor('qmljs', 'MainWindow.qml', qmlCode);
      const symbols = extractor.extractSymbols(parseResult.tree);

      expect(symbols.length).toBeGreaterThanOrEqual(7);

      // Check main application window
      const app = symbols.find(s => s.name === 'app' && s.kind === SymbolKind.Class);
      expect(app).toBeDefined();

      // Check layout components
      const layout = symbols.find(s => s.name === 'layout' && s.kind === SymbolKind.Class);
      expect(layout).toBeDefined();

      const header = symbols.find(s => s.name === 'header' && s.kind === SymbolKind.Class);
      expect(header).toBeDefined();

      const scrollArea = symbols.find(s => s.name === 'scrollArea' && s.kind === SymbolKind.Class);
      expect(scrollArea).toBeDefined();

      const dataList = symbols.find(s => s.name === 'dataList' && s.kind === SymbolKind.Class);
      expect(dataList).toBeDefined();

      const dataModel = symbols.find(s => s.name === 'dataModel' && s.kind === SymbolKind.Class);
      expect(dataModel).toBeDefined();

      const statusBar = symbols.find(s => s.name === 'statusBar' && s.kind === SymbolKind.Class);
      expect(statusBar).toBeDefined();
    });
  });

  describe('QML States and Transitions', () => {
    it('should extract QML states, transitions, and animations', async () => {
      const qmlCode = `
import QtQuick 2.15

Rectangle {
    id: animatedBox
    width: 200
    height: 200
    color: "blue"

    property bool isExpanded: false

    states: [
        State {
            name: "collapsed"
            when: !animatedBox.isExpanded
            PropertyChanges {
                target: animatedBox
                width: 200
                height: 200
                color: "blue"
            }
        },
        State {
            name: "expanded"
            when: animatedBox.isExpanded
            PropertyChanges {
                target: animatedBox
                width: 400
                height: 300
                color: "red"
            }
        }
    ]

    transitions: [
        Transition {
            from: "collapsed"
            to: "expanded"
            NumberAnimation {
                properties: "width,height"
                duration: 300
                easing.type: Easing.OutQuad
            }
            ColorAnimation {
                duration: 300
            }
        },
        Transition {
            from: "expanded"
            to: "collapsed"
            NumberAnimation {
                properties: "width,height"
                duration: 200
                easing.type: Easing.InQuad
            }
            ColorAnimation {
                duration: 200
            }
        }
    ]

    function toggleExpansion() {
        isExpanded = !isExpanded;
    }

    MouseArea {
        anchors.fill: parent
        onClicked: parent.toggleExpansion()
    }

    Text {
        id: stateLabel
        text: parent.state || "default"
        anchors.centerIn: parent
        color: "white"
        font.pointSize: 16
    }
}
      `;

      const parseResult = await parserManager.parseFile('test.qml', qmlCode);
      const extractor = new QMLJSExtractor('qmljs', 'test.qml', qmlCode);
      const symbols = extractor.extractSymbols(parseResult.tree);

      expect(symbols.length).toBeGreaterThanOrEqual(4);

      // Check main component
      const animatedBox = symbols.find(s => s.name === 'animatedBox' && s.kind === SymbolKind.Class);
      expect(animatedBox).toBeDefined();

      // Check function
      const toggleExpansion = symbols.find(s => s.name === 'toggleExpansion' && s.kind === SymbolKind.Function);
      expect(toggleExpansion).toBeDefined();
      expect(toggleExpansion?.signature).toContain('toggleExpansion()');

      // Check text label
      const stateLabel = symbols.find(s => s.name === 'stateLabel' && s.kind === SymbolKind.Class);
      expect(stateLabel).toBeDefined();

      // Check property
      const isExpanded = symbols.find(s => s.name === 'isExpanded' && s.kind === SymbolKind.Property);
      expect(isExpanded).toBeDefined();
    });
  });

  describe('Qt Quick Controls and Advanced Components', () => {
    it('should extract Qt Quick Controls and their properties', async () => {
      const qmlCode = `
import QtQuick 2.15
import QtQuick.Controls 2.15
import QtQuick.Dialogs 1.3

ApplicationWindow {
    id: mainApp
    width: 640
    height: 480
    title: "Controls Demo"

    property string selectedFile: ""
    property int currentProgress: 0

    function openFileDialog() {
        fileDialog.open();
    }

    function updateProgress(value) {
        currentProgress = Math.max(0, Math.min(100, value));
        progressBar.value = currentProgress / 100.0;
    }

    header: ToolBar {
        id: toolBar

        RowLayout {
            anchors.fill: parent

            ToolButton {
                id: menuButton
                text: "☰"
                onClicked: drawer.open()
            }

            Label {
                id: titleLabel
                text: mainApp.title
                Layout.fillWidth: true
                horizontalAlignment: Qt.AlignHCenter
            }

            ToolButton {
                id: settingsButton
                text: "⚙"
                onClicked: settingsPopup.open()
            }
        }
    }

    Drawer {
        id: drawer
        width: 200
        height: mainApp.height

        ListView {
            id: menuList
            anchors.fill: parent
            model: ["File", "Edit", "View", "Help"]

            delegate: ItemDelegate {
                text: modelData
                width: parent.width
                onClicked: {
                    console.log("Menu item clicked:", modelData);
                    drawer.close();
                }
            }
        }
    }

    ColumnLayout {
        anchors.fill: parent
        anchors.margins: 20

        ProgressBar {
            id: progressBar
            Layout.fillWidth: true
            value: 0.0
            from: 0.0
            to: 1.0
        }

        Button {
            id: fileButton
            text: "Select File"
            Layout.alignment: Qt.AlignHCenter
            onClicked: mainApp.openFileDialog()
        }

        TextArea {
            id: textArea
            Layout.fillWidth: true
            Layout.fillHeight: true
            placehnewerText: "Enter text here..."
            selectByMouse: true
        }
    }

    Popup {
        id: settingsPopup
        x: (parent.width - width) / 2
        y: (parent.height - height) / 2
        width: 300
        height: 200
        modal: true

        ColumnLayout {
            anchors.fill: parent
            anchors.margins: 20

            Label {
                text: "Settings"
                font.pointSize: 16
            }

            CheckBox {
                id: autoSaveCheck
                text: "Auto-save"
                checked: true
            }

            SpinBox {
                id: intervalSpin
                from: 1
                to: 60
                value: 5
                suffix: " min"
            }

            Button {
                text: "Close"
                Layout.alignment: Qt.AlignHCenter
                onClicked: settingsPopup.close()
            }
        }
    }

    FileDialog {
        id: fileDialog
        title: "Select a file"
        nameFilters: ["Text files (*.txt)", "All files (*)"]

        onAccepted: {
            mainApp.selectedFile = fileUrl.toString();
            console.log("File selected:", mainApp.selectedFile);
        }
    }
}
      `;

      const parseResult = await parserManager.parseFile('test.qml', qmlCode);
      const extractor = new QMLJSExtractor('qmljs', 'test.qml', qmlCode);
      const symbols = extractor.extractSymbols(parseResult.tree);

      expect(symbols.length).toBeGreaterThanOrEqual(15);

      // Check main application
      const mainApp = symbols.find(s => s.name === 'mainApp' && s.kind === SymbolKind.Class);
      expect(mainApp).toBeDefined();

      // Check functions
      const openFileDialog = symbols.find(s => s.name === 'openFileDialog' && s.kind === SymbolKind.Function);
      expect(openFileDialog).toBeDefined();

      const updateProgress = symbols.find(s => s.name === 'updateProgress' && s.kind === SymbolKind.Function);
      expect(updateProgress).toBeDefined();
      expect(updateProgress?.signature).toContain('updateProgress(value)');

      // Check major components
      const toolBar = symbols.find(s => s.name === 'toolBar' && s.kind === SymbolKind.Class);
      expect(toolBar).toBeDefined();

      const drawer = symbols.find(s => s.name === 'drawer' && s.kind === SymbolKind.Class);
      expect(drawer).toBeDefined();

      const progressBar = symbols.find(s => s.name === 'progressBar' && s.kind === SymbolKind.Class);
      expect(progressBar).toBeDefined();

      const textArea = symbols.find(s => s.name === 'textArea' && s.kind === SymbolKind.Class);
      expect(textArea).toBeDefined();

      const settingsPopup = symbols.find(s => s.name === 'settingsPopup' && s.kind === SymbolKind.Class);
      expect(settingsPopup).toBeDefined();

      const fileDialog = symbols.find(s => s.name === 'fileDialog' && s.kind === SymbolKind.Class);
      expect(fileDialog).toBeDefined();

      // Check properties
      const selectedFile = symbols.find(s => s.name === 'selectedFile' && s.kind === SymbolKind.Property);
      expect(selectedFile).toBeDefined();

      const currentProgress = symbols.find(s => s.name === 'currentProgress' && s.kind === SymbolKind.Property);
      expect(currentProgress).toBeDefined();
    });
  });

  describe('Type Inference', () => {
    it('should infer types for QML properties and JavaScript variables', async () => {
      const qmlCode = `
import QtQuick 2.15

Rectangle {
    id: root

    property string title: "Test"
    property int counter: 42
    property real percentage: 75.5
    property bool isActive: true
    property var userData: {"name": "John", "age": 30}
    property list<Item> childItems
    property alias mainText: textLabel.text

    function calculateValue(input) {
        let result = input * 2;
        return result + 10;
    }

    Text {
        id: textLabel
        text: "Hello QML"
    }
}
      `;

      const parseResult = await parserManager.parseFile('test.qml', qmlCode);
      const extractor = new QMLJSExtractor('qmljs', 'test.qml', qmlCode);
      const symbols = extractor.extractSymbols(parseResult.tree);
      const types = extractor.inferTypes(symbols);

      expect(types.size).toBeGreaterThan(0);

      // Check inferred types for properties
      expect(types.get('title')).toBe('string');
      expect(types.get('counter')).toBe('int');
      expect(types.get('percentage')).toBe('real');
      expect(types.get('isActive')).toBe('bool');
      expect(types.get('userData')).toBe('var');
      expect(types.get('childItems')).toBe('list<Item>');

      // Check function type
      expect(types.get('calculateValue')).toContain('function');
    });
  });
});