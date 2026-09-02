namespace Miller.Core.Resolution;

/// <summary>Pure query-time resolution driver. All I/O stays behind <see cref="IResolutionFacts"/>.</summary>
public sealed class QueryTimeResolver(IResolutionFacts facts)
{
    public ResolutionOutcome Resolve(ResolutionInput input)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(input.Name);
        ArgumentNullException.ThrowIfNull(input.Language);

        if (input.Name.Length == 0)
            return ResolutionOutcome.NoContext;

        if (input.Origin == ResolutionOrigin.Pending
            && input.RefKind == ResolutionRefKind.Instantiates
            && string.Equals(input.Language, "qml", StringComparison.Ordinal))
            return ResolveQmlInstantiation(input);

        bool hasReceiver = input.Receiver is { Length: > 0 } || input.ReceiverType is { Length: > 0 };
        IReadOnlyList<ResolutionTier> chain = ResolutionPolicy.Chain(input.Origin, input.RefKind, hasReceiver);
        if (chain.Count == 0)
            return ResolutionOutcome.NoContext;

        bool attempted = false;
        int? firstAmbiguousCount = null;
        foreach (ResolutionTier tier in chain)
        {
            if (tier == ResolutionTier.Import && !ResolutionPolicy.IsTier2Language(input.Language))
                continue;

            attempted = true;
            Dictionary<FactSymbolKey, double> acc = Collect(tier, input);
            if (acc.Count == 1)
            {
                FactSymbolKey target = default;
                double confidence = 0;
                foreach ((FactSymbolKey key, double value) in acc)
                {
                    target = key;
                    confidence = value;
                }

                return ResolutionOutcome.Resolved(
                    target,
                    ResolutionPolicy.TierNumber(tier),
                    Math.Min(confidence, input.SourceConfidence),
                    ResolutionPolicy.TierMethod(tier));
            }

            if (acc.Count > 1)
                firstAmbiguousCount ??= acc.Count;
        }

        return firstAmbiguousCount is { } count
            ? ResolutionOutcome.Ambiguous(count)
            : attempted ? ResolutionOutcome.Missing : ResolutionOutcome.NoContext;
    }

    private ResolutionOutcome ResolveQmlInstantiation(ResolutionInput input)
    {
        if (input.ConsumerPath is not { Length: > 0 })
            return ResolutionOutcome.Missing;

        string? alias = input.Receiver is { Length: > 0 } receiver ? receiver : null;
        var request = new QmlVisibilityRequest(input.VersionId, input.ConsumerPath, input.Name, ImportAlias: alias);
        IReadOnlyList<QmlVisibleType> visible = QmlVisibilityPolicy.FilterAndOrder(
            facts.QmlTypesVisibleTo(input.VersionId),
            request);
        if (visible.Count == 0)
            return ResolutionOutcome.Missing;
        if (visible.Count > 1)
            return ResolutionOutcome.Ambiguous(visible.Count);

        QmlVisibleType candidate = visible[0];
        bool local = QmlVisibilityPolicy.ScopeStrength(candidate, request) <= 1;
        ResolutionTier tier = local ? ResolutionTier.Local : ResolutionTier.Import;
        return ResolutionOutcome.Resolved(
            candidate.Target,
            ResolutionPolicy.TierNumber(tier),
            Math.Min(tier == ResolutionTier.Local ? ResolutionPolicy.LocalConfidence : ResolutionPolicy.ImportConfidence, input.SourceConfidence),
            ResolutionPolicy.TierMethod(tier));
    }

    private Dictionary<FactSymbolKey, double> Collect(ResolutionTier tier, ResolutionInput input) => tier switch
    {
        ResolutionTier.Local => LocalCandidates(input),
        ResolutionTier.Import => ImportCandidates(input),
        ResolutionTier.Receiver => ReceiverCandidates(input),
        ResolutionTier.StaticType => StaticTypeCandidates(input),
        _ => GlobalCandidates(input),
    };

    private Dictionary<FactSymbolKey, double> LocalCandidates(ResolutionInput input)
    {
        var acc = new Dictionary<FactSymbolKey, double>();
        IReadOnlySet<FactSymbolKind> kinds = ResolutionPolicy.CompatibleKinds(input.RefKind, tier4: false);
        foreach (FactSymbol symbol in ScopeLevels(input.VersionId, input.CallerScopeSymbolId, input.Name, input.Language, kinds))
            KeepMax(acc, symbol.Key, ResolutionPolicy.LocalConfidence);
        return acc;
    }

    private Dictionary<FactSymbolKey, double> ImportCandidates(ResolutionInput input)
    {
        var acc = new Dictionary<FactSymbolKey, double>();
        IReadOnlySet<FactSymbolKind> kinds = ResolutionPolicy.CompatibleKinds(input.RefKind, tier4: false);
        foreach (ImportBinding import in facts.ImportsOf(input.VersionId))
        {
            if (import.IsTypeOnly || import.IsNamespace || import.IsDefault)
                continue;
            if (import.LocalName != input.Name)
                continue;
            if (import.Source is not null && import.ModuleVersionId is null)
                continue;

            string targetName = import.ImportedName ?? import.LocalName;
            foreach (FactSymbol symbol in facts.SymbolsNamed(targetName))
            {
                if (symbol.Language != input.Language || !kinds.Contains(symbol.Kind))
                    continue;
                if (import.ModuleVersionId is { } module && symbol.Key.VersionId != module)
                    continue;
                KeepMax(acc, symbol.Key, ResolutionPolicy.ImportConfidence);
            }
        }

        return acc;
    }

    private Dictionary<FactSymbolKey, double> ReceiverCandidates(ResolutionInput input)
    {
        var acc = new Dictionary<FactSymbolKey, double>();
        IReadOnlySet<FactSymbolKind> kinds = ResolutionPolicy.CompatibleKinds(input.RefKind, tier4: false);

        if (input.ReceiverType is { Length: > 0 } receiverType)
            AddReceiverTypeMembers(acc, input, receiverType, ResolutionPolicy.ReceiverDeclaredConfidence, kinds);

        if (input.Receiver is not { Length: > 0 })
            return acc;

        foreach (FactSymbol receiver in ScopeLevels(input.VersionId, input.CallerScopeSymbolId, input.Receiver, input.Language, kinds: null))
        {
            foreach (FactTypeFact fact in facts.TypeFactsOf(receiver.Key))
            {
                double confidence = fact.IsInferred
                    ? ResolutionPolicy.ReceiverInferredConfidence
                    : ResolutionPolicy.ReceiverDeclaredConfidence;
                AddReceiverTypeMembers(acc, input, fact.ResolvedType, confidence, kinds);
            }
        }

        return acc;
    }

    private void AddReceiverTypeMembers(
        Dictionary<FactSymbolKey, double> acc,
        ResolutionInput input,
        string typeName,
        double confidence,
        IReadOnlySet<FactSymbolKind> kinds)
    {
        FactSymbol? type = UniqueType(typeName, input.Language, ResolutionPolicy.TypeLike);
        if (type is null)
            return;

        foreach (FactSymbol member in facts.ChildrenOf(type.Key))
        {
            if (member.Name != input.Name || member.Language != input.Language || !kinds.Contains(member.Kind))
                continue;
            KeepMax(acc, member.Key, confidence);
        }
    }

    private Dictionary<FactSymbolKey, double> StaticTypeCandidates(ResolutionInput input)
    {
        var acc = new Dictionary<FactSymbolKey, double>();
        if (input.Receiver is not { Length: > 0 })
            return acc;
        if (ScopeBindsReceiverName(input.VersionId, input.CallerScopeSymbolId, input.Receiver))
            return acc;

        FactSymbol? type = ResolveStaticType(input.Receiver, input.Language, input.VersionId);
        if (type is null)
            return acc;
        if (!StaticReceiverReachable(type, input.ReceiverQualifier, input.VersionId))
            return acc;
        if (!StaticTypeImportCorroborated(type, input.Receiver, input.Language, input.VersionId))
            return acc;

        IReadOnlySet<FactSymbolKind> kinds = ResolutionPolicy.CompatibleKinds(input.RefKind, tier4: false);
        bool crossFile = type.Key.VersionId != input.VersionId;
        foreach (FactSymbol member in facts.ChildrenOf(type.Key))
        {
            if (member.Name != input.Name || member.Language != input.Language || !kinds.Contains(member.Kind))
                continue;
            if (!IsStaticallyReachable(member))
                continue;
            if (crossFile && !MemberCrossFileVisible(member))
                continue;
            KeepMax(acc, member.Key, ResolutionPolicy.StaticTypeConfidence);
        }

        return acc;
    }

    private Dictionary<FactSymbolKey, double> GlobalCandidates(ResolutionInput input)
    {
        var acc = new Dictionary<FactSymbolKey, double>();
        IReadOnlySet<FactSymbolKind> kinds = ResolutionPolicy.CompatibleKinds(input.RefKind, tier4: true);
        if (kinds.Count == 0)
            return acc;

        bool sameVersionOnly = ResolutionPolicy.IsEsModuleLanguage(input.Language);
        foreach (FactSymbol symbol in facts.SymbolsNamed(input.Name))
        {
            if (symbol.Language != input.Language || !kinds.Contains(symbol.Kind))
                continue;
            if (sameVersionOnly && symbol.Key.VersionId != input.VersionId)
                continue;
            KeepMax(acc, symbol.Key, ResolutionPolicy.GlobalConfidence);
        }

        return acc;
    }

    private IEnumerable<FactSymbol> ScopeLevels(
        long versionId,
        string? scopeId,
        string name,
        string language,
        IReadOnlySet<FactSymbolKind>? kinds)
    {
        string? current = scopeId;
        while (current is not null)
        {
            var scopeKey = new FactSymbolKey(versionId, current);
            List<FactSymbol>? hits = null;
            foreach (FactSymbol child in facts.ChildrenOf(scopeKey))
            {
                if (!NameLanguageKindMatch(child, name, language, kinds))
                    continue;
                hits ??= [];
                hits.Add(child);
            }

            if (hits is { Count: > 0 })
            {
                foreach (FactSymbol hit in hits)
                    yield return hit;
                yield break;
            }

            current = facts.Symbol(scopeKey)?.Parent?.SymbolId;
        }

        foreach (FactSymbol top in facts.TopLevelOf(versionId))
        {
            if (NameLanguageKindMatch(top, name, language, kinds))
                yield return top;
        }
    }

    private static bool NameLanguageKindMatch(
        FactSymbol symbol,
        string name,
        string language,
        IReadOnlySet<FactSymbolKind>? kinds) =>
        symbol.Name == name
        && symbol.Language == language
        && (kinds is null || kinds.Contains(symbol.Kind));

    private FactSymbol? UniqueType(string name, string language, IReadOnlySet<FactSymbolKind> kinds)
    {
        FactSymbol? found = null;
        foreach (FactSymbol symbol in facts.SymbolsNamed(name))
        {
            if (symbol.Language != language || !kinds.Contains(symbol.Kind))
                continue;
            if (found is not null)
                return null;
            found = symbol;
        }

        return found;
    }

    private bool ScopeBindsReceiverName(long versionId, string? scopeId, string receiver)
    {
        string? current = scopeId;
        while (current is not null)
        {
            var key = new FactSymbolKey(versionId, current);
            FactSymbol? scope = facts.Symbol(key);
            if (scope is null)
                return false;
            if (ResolutionPolicy.IsTypeLike(scope.Kind))
                return false;

            foreach (FactSymbol child in facts.ChildrenOf(key))
            {
                if (child.Name == receiver && child.Kind == FactSymbolKind.Variable)
                    return true;
            }

            if (scope.Signature is { } signature && SignatureDeclaresParameter(signature, receiver))
                return true;

            current = scope.Parent?.SymbolId;
        }

        return false;
    }

    internal static bool SignatureDeclaresParameter(string signature, string name)
    {
        int open = signature.IndexOf('(');
        if (open < 0)
            return false;

        int depth = 0;
        int close = -1;
        for (int i = open; i < signature.Length; i++)
        {
            char c = signature[i];
            if (c is '(' or '<' or '[' or '{')
                depth++;
            else if (c is ')' or '>' or ']' or '}')
            {
                depth--;
                if (depth == 0 && c == ')')
                {
                    close = i;
                    break;
                }
            }
        }

        if (close < 0)
            return false;

        foreach (string param in SplitTopLevel(signature[(open + 1)..close]))
        {
            string text = param;
            int eq = TopLevelIndexOf(text, '=');
            if (eq >= 0)
                text = text[..eq];
            string[] tokens = text.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length > 0 && tokens[^1].TrimStart('@') == name)
                return true;
        }

        return false;
    }

    private static IEnumerable<string> SplitTopLevel(string text)
    {
        int depth = 0;
        int start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c is '(' or '<' or '[' or '{')
                depth++;
            else if (c is ')' or '>' or ']' or '}')
                depth--;
            else if (c == ',' && depth == 0)
            {
                yield return text[start..i];
                start = i + 1;
            }
        }

        if (start < text.Length)
            yield return text[start..];
    }

    private static int TopLevelIndexOf(string text, char target)
    {
        int depth = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c is '(' or '<' or '[' or '{')
                depth++;
            else if (c is ')' or '>' or ']' or '}')
                depth--;
            else if (c == target && depth == 0)
                return i;
        }

        return -1;
    }

    private FactSymbol? ResolveStaticType(string receiver, string language, long versionId)
    {
        IReadOnlySet<FactSymbolKind> kinds = ResolutionPolicy.IsEsModuleLanguage(language)
            ? ResolutionPolicy.EsModuleStaticTypeKinds
            : ResolutionPolicy.TypeLike;
        FactSymbol? direct = UniqueType(receiver, language, kinds);
        if (direct is not null)
            return direct;
        if (!ResolutionPolicy.IsEsModuleLanguage(language))
            return null;

        FactSymbol? found = null;
        foreach (ImportBinding import in facts.ImportsOf(versionId))
        {
            if (import.IsTypeOnly || import.IsNamespace)
                continue;
            if (import.LocalName != receiver)
                continue;
            if (import.ImportedName is not { Length: > 0 } importedName || importedName == receiver)
                continue;

            FactSymbol? type = UniqueType(importedName, language, kinds);
            if (type is null)
                continue;
            if (import.ModuleVersionId is { } module && type.Key.VersionId != module)
                continue;
            if (found is not null && found.Key != type.Key)
                return null;
            found = type;
        }

        return found;
    }

    private bool StaticReceiverReachable(FactSymbol type, string? qualifier, long fromVersion)
    {
        if (type.Parent is { } parentKey
            && facts.Symbol(parentKey) is { } parent
            && ResolutionPolicy.IsTypeLike(parent.Kind))
        {
            return false;
        }

        if (qualifier is { Length: > 0 })
        {
            List<string> declared = DeclaredNamespacePath(type);
            string[] wanted = [.. qualifier.Split('.').Where(part => part.Length > 0 && part != "global")];
            if (wanted.Length > declared.Count)
                return false;
            int offset = declared.Count - wanted.Length;
            for (int i = 0; i < wanted.Length; i++)
            {
                if (declared[offset + i] != wanted[i])
                    return false;
            }
        }

        if (type.Key.VersionId == fromVersion)
            return true;
        return TypeCrossFileVisible(type);
    }

    private static bool TypeCrossFileVisible(FactSymbol type) => type.Visibility is "public" or "internal";

    private List<string> DeclaredNamespacePath(FactSymbol type)
    {
        var ancestors = new List<string>();
        FactSymbolKey? current = type.Parent;
        while (current is { } key && facts.Symbol(key) is { } symbol)
        {
            if (symbol.Kind is FactSymbolKind.Namespace or FactSymbolKind.Module)
                ancestors.Add(symbol.Name);
            current = symbol.Parent;
        }

        ancestors.Reverse();
        var path = new List<string>();
        foreach (string ancestor in ancestors)
        {
            foreach (string part in ancestor.Split('.'))
            {
                if (part.Length > 0)
                    path.Add(part);
            }
        }

        return path;
    }

    private bool StaticTypeImportCorroborated(FactSymbol type, string receiver, string language, long fromVersion)
    {
        if (type.Key.VersionId == fromVersion)
            return true;
        if (!ResolutionPolicy.IsEsModuleLanguage(language))
            return true;

        foreach (ImportBinding import in facts.ImportsOf(fromVersion))
        {
            if (import.IsTypeOnly || import.IsNamespace || import.IsDefault)
                continue;
            if (import.LocalName != receiver)
                continue;
            if (import.ModuleVersionId != type.Key.VersionId)
                continue;
            if ((import.ImportedName ?? import.LocalName) == type.Name)
                return true;
        }

        return false;
    }

    private static bool IsStaticallyReachable(FactSymbol member)
    {
        if (member.Kind is FactSymbolKind.EnumMember or FactSymbolKind.Constant or FactSymbolKind.Enum)
            return true;
        if (member.IsStatic is { } isStatic)
            return isStatic;
        return SignatureHasStaticModifier(member.Signature);
    }

    internal static bool SignatureHasStaticModifier(string? signature)
    {
        if (signature is null)
            return false;

        string text = signature;
        while (true)
        {
            int open = text.IndexOf('[');
            if (open < 0)
                break;
            int depth = 0;
            int close = -1;
            for (int i = open; i < text.Length; i++)
            {
                if (text[i] == '[')
                    depth++;
                else if (text[i] == ']')
                {
                    depth--;
                    if (depth == 0)
                    {
                        close = i;
                        break;
                    }
                }
            }

            if (close < 0)
                break;
            text = text[..open] + text[(close + 1)..];
        }

        int stop = text.Length;
        foreach (char marker in stackalloc[] { '(', '<', '=', '{', '"' })
        {
            int at = text.IndexOf(marker);
            if (at >= 0 && at < stop)
                stop = at;
        }

        return text[..stop].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Contains("static");
    }

    private static bool MemberCrossFileVisible(FactSymbol member) => member.Visibility switch
    {
        null or "public" or "open" or "internal" => true,
        "private" or "protected" or "fileprivate" => false,
        _ => true,
    };

    private static void KeepMax(Dictionary<FactSymbolKey, double> acc, FactSymbolKey key, double confidence)
    {
        if (!acc.TryGetValue(key, out double existing) || confidence > existing)
            acc[key] = confidence;
    }
}
