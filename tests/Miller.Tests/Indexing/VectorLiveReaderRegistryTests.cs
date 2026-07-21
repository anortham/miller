using Miller.Indexing.Semantic;
using Xunit;

namespace Miller.Tests.Indexing;

/// <summary>
/// The in-process live-reader registry that connects the reader arm's open sites to the leader's GC scheduler:
/// a refcount whose <see cref="VectorLiveReaderRegistry.LiveTags"/> snapshot GC consults, protected against
/// double-dispose and concurrent register/release.
/// </summary>
public sealed class VectorLiveReaderRegistryTests
{
    private const string Tag = "aaaaaaaaaaaaaaaa";

    [Fact]
    public void Register_MakesTheTagLive_AndDisposeReleasesIt()
    {
        var registry = new VectorLiveReaderRegistry();

        IDisposable registration = registry.Register(Tag);
        Assert.Contains(Tag, registry.LiveTags);

        registration.Dispose();
        Assert.DoesNotContain(Tag, registry.LiveTags);
    }

    [Fact]
    public void Register_Refcounts_SoTheTagStaysLiveUntilTheLastReaderDisposes()
    {
        var registry = new VectorLiveReaderRegistry();

        IDisposable first = registry.Register(Tag);
        IDisposable second = registry.Register(Tag);

        first.Dispose();
        Assert.Contains(Tag, registry.LiveTags);

        second.Dispose();
        Assert.DoesNotContain(Tag, registry.LiveTags);
    }

    [Fact]
    public void Dispose_IsIdempotent_SoADoubleDisposeDoesNotUnderflowTheRefcount()
    {
        var registry = new VectorLiveReaderRegistry();

        IDisposable held = registry.Register(Tag);
        IDisposable doubleDisposed = registry.Register(Tag);

        doubleDisposed.Dispose();
        doubleDisposed.Dispose();

        Assert.Contains(Tag, registry.LiveTags);

        held.Dispose();
        Assert.DoesNotContain(Tag, registry.LiveTags);
    }

    [Fact]
    public void LiveTags_IsASnapshot_NotAffectedByLaterRegistrations()
    {
        var registry = new VectorLiveReaderRegistry();

        using IDisposable _ = registry.Register(Tag);
        IReadOnlySet<string> snapshot = registry.LiveTags;

        using IDisposable other = registry.Register("bbbbbbbbbbbbbbbb");

        Assert.DoesNotContain("bbbbbbbbbbbbbbbb", snapshot);
        Assert.Contains("bbbbbbbbbbbbbbbb", registry.LiveTags);
    }

    [Fact]
    public void Register_RejectsAnEmptyTag()
    {
        var registry = new VectorLiveReaderRegistry();

        Assert.Throws<ArgumentException>(() => registry.Register(string.Empty));
    }

    [Fact]
    public async Task Register_IsThreadSafe_UnderConcurrentRegisterAndRelease()
    {
        var registry = new VectorLiveReaderRegistry();
        const int workers = 16;
        const int iterations = 500;

        await Task.WhenAll(Enumerable.Range(0, workers).Select(worker => Task.Run(() =>
        {
            string tag = $"tag-{worker % 4}";
            for (int i = 0; i < iterations; i++)
            {
                IDisposable registration = registry.Register(tag);
                _ = registry.LiveTags;
                registration.Dispose();
            }
        })));

        Assert.Empty(registry.LiveTags);
    }
}
