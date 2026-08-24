import QtTest

TestCase {
    name: "Second"

    function test_concatenation() {
        compare(["qml", "test"].join(" "), "qml test")
    }
}
