using Miller.Tests.Indexing.Resolution;

namespace Miller.Tests.Support;

internal static class QmlVisibilityFixtureSupport
{
    internal static readonly string[] ExpectedExportedNames =
        ["LocalCard", "RemoteCard", "RemoteCard", "Theme", "Theme"];

    internal static void Populate(ResolutionStoreFixture fixture)
    {
        fixture.AddFile(1, "source.qml", "qml");
        fixture.AddFile(2, "LocalCard.qml", "qml");
        fixture.AddFile(3, "components/RemoteCard.qml", "qml");
        fixture.AddFile(4, "components/Theme.qml", "qml");
        fixture.AddFile(5, "components/qmldir", "qmldir");
        fixture.AddFile(6, "components/Module.qmltypes", "qml");
        fixture.AddFile(7, "components/InternalCard.qml", "qml");

        AddSymbols(fixture);
        AddStructuralFacts(fixture);
    }

    internal static void Populate(ResolutionArtifactFixture fixture)
    {
        fixture.AddFile("file-source", "source.qml", "qml");
        fixture.AddFile("file-local", "LocalCard.qml", "qml");
        fixture.AddFile("file-remote", "components/RemoteCard.qml", "qml");
        fixture.AddFile("file-theme", "components/Theme.qml", "qml");
        fixture.AddFile("file-qmldir", "components/qmldir", "qmldir");
        fixture.AddFile("file-typeinfo", "components/Module.qmltypes", "qml");
        fixture.AddFile("file-internal", "components/InternalCard.qml", "qml");

        AddSymbols(fixture);
        AddStructuralFacts(fixture);
    }

    private static void AddSymbols(ResolutionStoreFixture fixture)
    {
        fixture.AddSymbol(1, "source", "source", "class", "source.qml");
        fixture.AddSymbol(
            1,
            "import-components",
            "components",
            "import",
            "source.qml",
            language: "qml",
            metadataJson: """{"import_kind":"directory","source":"components","alias":"Components","local_name":"Components","is_namespace":true}""");
        fixture.AddSymbol(
            1,
            "import-module",
            "Example.Components",
            "import",
            "source.qml",
            language: "qml",
            metadataJson: """{"import_kind":"module","source":"Example.Components","alias":"EC","local_name":"EC","version":"1.0","is_namespace":true}""");
        fixture.AddSymbol(2, "local", "LocalCard", "class", "LocalCard.qml", language: "qml");
        fixture.AddSymbol(3, "remote", "RemoteCard", "class", "components/RemoteCard.qml", language: "qml");
        fixture.AddSymbol(4, "theme", "Theme", "class", "components/Theme.qml", language: "qml");
        fixture.AddSymbol(
            5,
            "module",
            "Example.Components",
            "module",
            "components/qmldir",
            language: "qmldir",
            metadataJson: """{"module_name":"Example.Components","qmldir_kind":"module"}""");
        fixture.AddSymbol(
            5,
            "remote-export",
            "RemoteCard",
            "class",
            "components/qmldir",
            language: "qmldir",
            metadataJson: """{"file":"RemoteCard.qml","qmldir_kind":"object_type","type_name":"RemoteCard","version":"1.0"}""");
        fixture.AddSymbol(
            5,
            "theme-export",
            "Theme",
            "class",
            "components/qmldir",
            language: "qmldir",
            metadataJson: """{"file":"Theme.qml","qmldir_kind":"singleton","singleton":true,"type_name":"Theme","version":"1.0"}""");
        fixture.AddSymbol(
            6,
            "remote-typeinfo",
            "RemoteCard",
            "class",
            "components/Module.qmltypes",
            language: "qml",
            metadataJson: """{"exports":["Example/Components 1.0"],"typeinfo_kind":"type"}""");
        fixture.AddSymbol(7, "internal", "InternalCard", "class", "components/InternalCard.qml", language: "qml");
        fixture.AddSymbol(
            5,
            "internal-export",
            "InternalCard",
            "class",
            "components/qmldir",
            language: "qmldir",
            visibility: "internal",
            metadataJson: """{"file":"InternalCard.qml","internal":true,"qmldir_kind":"internal","type_name":"InternalCard"}""");
    }

    private static void AddSymbols(ResolutionArtifactFixture fixture)
    {
        fixture.AddSymbol("file-source", "source", "source", "class", "source.qml", language: "qml");
        fixture.AddSymbol(
            "file-source",
            "import-components",
            "components",
            "import",
            "source.qml",
            language: "qml",
            metadataJson: """{"import_kind":"directory","source":"components","alias":"Components","local_name":"Components","is_namespace":true}""");
        fixture.AddSymbol(
            "file-source",
            "import-module",
            "Example.Components",
            "import",
            "source.qml",
            language: "qml",
            metadataJson: """{"import_kind":"module","source":"Example.Components","alias":"EC","local_name":"EC","version":"1.0","is_namespace":true}""");
        fixture.AddSymbol("file-local", "local", "LocalCard", "class", "LocalCard.qml", language: "qml");
        fixture.AddSymbol("file-remote", "remote", "RemoteCard", "class", "components/RemoteCard.qml", language: "qml");
        fixture.AddSymbol("file-theme", "theme", "Theme", "class", "components/Theme.qml", language: "qml");
        fixture.AddSymbol(
            "file-qmldir",
            "module",
            "Example.Components",
            "module",
            "components/qmldir",
            language: "qmldir",
            metadataJson: """{"module_name":"Example.Components","qmldir_kind":"module"}""");
        fixture.AddSymbol(
            "file-qmldir",
            "remote-export",
            "RemoteCard",
            "class",
            "components/qmldir",
            language: "qmldir",
            metadataJson: """{"file":"RemoteCard.qml","qmldir_kind":"object_type","type_name":"RemoteCard","version":"1.0"}""");
        fixture.AddSymbol(
            "file-qmldir",
            "theme-export",
            "Theme",
            "class",
            "components/qmldir",
            language: "qmldir",
            metadataJson: """{"file":"Theme.qml","qmldir_kind":"singleton","singleton":true,"type_name":"Theme","version":"1.0"}""");
        fixture.AddSymbol(
            "file-typeinfo",
            "remote-typeinfo",
            "RemoteCard",
            "class",
            "components/Module.qmltypes",
            language: "qml",
            metadataJson: """{"exports":["Example/Components 1.0"],"typeinfo_kind":"type"}""");
        fixture.AddSymbol("file-internal", "internal", "InternalCard", "class", "components/InternalCard.qml", language: "qml");
        fixture.AddSymbol(
            "file-qmldir",
            "internal-export",
            "InternalCard",
            "class",
            "components/qmldir",
            language: "qmldir",
            metadataJson: """{"file":"InternalCard.qml","internal":true,"qmldir_kind":"internal","type_name":"InternalCard"}""");
    }

    private static void AddStructuralFacts(ResolutionStoreFixture fixture)
    {
        fixture.AddStructuralFact(
            5,
            "fact-module",
            "components/qmldir",
            "qmldir.module.v1",
            "module",
            "command",
            0,
            20,
            """{"directive":"module","module":"Example.Components","pattern_version":1,"query_family":"qmldir"}""");
        fixture.AddStructuralFact(
            5,
            "fact-remote",
            "components/qmldir",
            "qmldir.object_type.v1",
            "object_type",
            "command",
            20,
            40,
            """{"directive":"object_type","file":"RemoteCard.qml","pattern_version":1,"query_family":"qmldir","type_name":"RemoteCard","version":"1.0"}""");
        fixture.AddStructuralFact(
            5,
            "fact-theme",
            "components/qmldir",
            "qmldir.singleton_type.v1",
            "singleton_type",
            "command",
            40,
            60,
            """{"directive":"singleton","file":"Theme.qml","pattern_version":1,"query_family":"qmldir","singleton":true,"type_name":"Theme","version":"1.0"}""");
        fixture.AddStructuralFact(
            5,
            "fact-typeinfo",
            "components/qmldir",
            "qmldir.typeinfo.v1",
            "typeinfo",
            "command",
            60,
            80,
            """{"directive":"typeinfo","file":"Module.qmltypes","pattern_version":1,"query_family":"qmldir"}""");
        fixture.AddStructuralFact(
            6,
            "fact-type",
            "components/Module.qmltypes",
            "qml.typeinfo_declaration.v1",
            "typeinfo_declaration",
            "ui_object_definition",
            0,
            20,
            """{"pattern_version":1,"query_family":"typeinfo","type_name":"RemoteCard","typeinfo_kind":"type"}""");
        fixture.AddStructuralFact(
            5,
            "fact-internal",
            "components/qmldir",
            "qmldir.internal_type.v1",
            "internal_type",
            "command",
            80,
            100,
            """{"directive":"internal","file":"InternalCard.qml","internal":true,"pattern_version":1,"query_family":"qmldir","type_name":"InternalCard"}""",
            language: "qmldir");
    }

    private static void AddStructuralFacts(ResolutionArtifactFixture fixture)
    {
        fixture.AddStructuralFact(
            "file-qmldir",
            "fact-module",
            "components/qmldir",
            "qmldir.module.v1",
            "module",
            "command",
            0,
            20,
            """{"directive":"module","module":"Example.Components","pattern_version":1,"query_family":"qmldir"}""",
            language: "qmldir");
        fixture.AddStructuralFact(
            "file-qmldir",
            "fact-remote",
            "components/qmldir",
            "qmldir.object_type.v1",
            "object_type",
            "command",
            20,
            40,
            """{"directive":"object_type","file":"RemoteCard.qml","pattern_version":1,"query_family":"qmldir","type_name":"RemoteCard","version":"1.0"}""",
            language: "qmldir");
        fixture.AddStructuralFact(
            "file-qmldir",
            "fact-theme",
            "components/qmldir",
            "qmldir.singleton_type.v1",
            "singleton_type",
            "command",
            40,
            60,
            """{"directive":"singleton","file":"Theme.qml","pattern_version":1,"query_family":"qmldir","singleton":true,"type_name":"Theme","version":"1.0"}""",
            language: "qmldir");
        fixture.AddStructuralFact(
            "file-qmldir",
            "fact-typeinfo",
            "components/qmldir",
            "qmldir.typeinfo.v1",
            "typeinfo",
            "command",
            60,
            80,
            """{"directive":"typeinfo","file":"Module.qmltypes","pattern_version":1,"query_family":"qmldir"}""",
            language: "qmldir");
        fixture.AddStructuralFact(
            "file-typeinfo",
            "fact-type",
            "components/Module.qmltypes",
            "qml.typeinfo_declaration.v1",
            "typeinfo_declaration",
            "ui_object_definition",
            0,
            20,
            """{"pattern_version":1,"query_family":"typeinfo","type_name":"RemoteCard","typeinfo_kind":"type"}""");
        fixture.AddStructuralFact(
            "file-qmldir",
            "fact-internal",
            "components/qmldir",
            "qmldir.internal_type.v1",
            "internal_type",
            "command",
            80,
            100,
            """{"directive":"internal","file":"InternalCard.qml","internal":true,"pattern_version":1,"query_family":"qmldir","type_name":"InternalCard"}""",
            language: "qmldir");
    }
}
