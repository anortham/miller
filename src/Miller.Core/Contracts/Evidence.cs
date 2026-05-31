namespace Miller.Core.Contracts;

/// <summary>
/// One piece of locating evidence for a bridge edge or a firing signal: a <c>file:line</c> the claim can be traced to
/// (a CreateMap call-site, an <c>[HttpGet]</c> annotation, a DbSet property declaration, a url literal). Pure data; the
/// <c>trace</c> tool renders it so a user can verify the edge by hand — nothing is presented as certain that cannot be
/// pointed at.
/// </summary>
/// <param name="FilePath">The workspace-relative file the evidence lives in.</param>
/// <param name="Line">The 1-based line of the evidence within that file.</param>
public sealed record Evidence(string FilePath, int Line);
