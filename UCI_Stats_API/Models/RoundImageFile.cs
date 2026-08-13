namespace UCI_Stats_API.Models;

public sealed record RoundImageFile(
    byte[] Bytes,
    string ContentType,
    string? FileName,
    string? AltText);
