Imports Microsoft.VisualStudio.TestTools.UnitTesting

<Assembly: DoNotParallelize>

<TestClass>
Public Class UnitTests
    <TestMethod>
    Public Sub Adds()
        Assert.AreEqual(2, 1 + 1)
    End Sub

    <DataTestMethod>
    <DataRow(1)>
    <DataRow(2)>
    Public Sub Positive(value As Integer)
        Assert.IsTrue(value > 0)
    End Sub
End Class
