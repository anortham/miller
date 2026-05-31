using Miller.Core.Contracts;
using Miller.Core.Resolver;
using Xunit;

namespace Miller.Tests.Resolver;

/// <summary>
/// Pins <see cref="FieldSetSimilarity"/> — the source of the scorer's <see cref="FieldSetSignal"/> corroborator. The
/// Jaccard is the field-NAME overlap (|A∩B| / |A∪B|, case-insensitive); the carried <see cref="FieldSetSignal.FieldCount"/>
/// is the MIN of the two shapes' distinct-name counts, so the §5 "1-field can't anchor" rule reads the smaller side.
/// Asserts the exact ratio and count.
/// </summary>
public sealed class FieldSetSimilarityTests
{
    private static FieldSet Set(string owner, params string[] names)
        => new(owner, [.. names.Select(n => new FieldMember(n, "string"))]);

    [Fact]
    public void Compare_IdenticalShapes_Jaccard1_FieldCountIsTheSharedCount()
    {
        var a = Set("A", "Id", "Name", "Email");
        var b = Set("B", "Id", "Name", "Email");

        var signal = FieldSetSimilarity.Compare(a, b);

        Assert.Equal(1.0, signal.Jaccard);
        Assert.Equal(3, signal.FieldCount);
    }

    [Fact]
    public void Compare_PartialOverlap_ComputesJaccard()
    {
        // A = {Id, Name, Email, Age}; B = {Id, Name, Phone} => intersection 2, union 5 => 0.4.
        var a = Set("A", "Id", "Name", "Email", "Age");
        var b = Set("B", "Id", "Name", "Phone");

        var signal = FieldSetSimilarity.Compare(a, b);

        Assert.Equal(0.4, signal.Jaccard, precision: 10);
        Assert.Equal(3, signal.FieldCount); // min(4, 3)
    }

    [Fact]
    public void Compare_FieldNamesAreCaseInsensitive()
    {
        var a = Set("A", "UserId", "Name");
        var b = Set("B", "userid", "NAME");

        var signal = FieldSetSimilarity.Compare(a, b);

        Assert.Equal(1.0, signal.Jaccard);
        Assert.Equal(2, signal.FieldCount);
    }

    [Fact]
    public void Compare_OneFieldSide_FieldCountIsOne_SoItCannotAnchor()
    {
        // The MIN rule is what makes "1-field can't anchor" decidable: a wide DTO vs a 1-field wrapper => FieldCount 1.
        var wide = Set("W", "Id", "Name", "Email", "Age", "Phone");
        var oneField = Set("O", "Id");

        var signal = FieldSetSimilarity.Compare(wide, oneField);

        Assert.Equal(1, signal.FieldCount);
    }

    [Fact]
    public void Compare_DisjointShapes_JaccardZero()
    {
        var a = Set("A", "Id", "Name");
        var b = Set("B", "Foo", "Bar");

        var signal = FieldSetSimilarity.Compare(a, b);

        Assert.Equal(0.0, signal.Jaccard);
    }

    [Fact]
    public void Compare_BothEmpty_JaccardZero_FieldCountZero()
    {
        var signal = FieldSetSimilarity.Compare(Set("A"), Set("B"));

        Assert.Equal(0.0, signal.Jaccard);
        Assert.Equal(0, signal.FieldCount);
    }

    [Fact]
    public void Jaccard_NullArgument_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => FieldSetSimilarity.Jaccard(null!, Set("B", "x")));
    }
}
