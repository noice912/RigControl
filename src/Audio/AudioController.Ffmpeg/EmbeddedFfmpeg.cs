using System.Reflection;

namespace AudioController.Ffmpeg;

/// <summary>
/// RigControl: the assembly that carries FFmpeg (ffmpeg.exe, ffprobe.exe and their notices) as embedded resources
/// named <c>ffmpeg/&lt;file&gt;</c>, with each file's SHA-256 in <c>[AssemblyMetadata("ffmpeg-sha256:&lt;file&gt;", ...)]</c>.
/// Read it with <c>AudioController.Decode.FfmpegExtractor.FromAssembly(EmbeddedFfmpeg.Assembly)</c>.
/// </summary>
public static class EmbeddedFfmpeg
{
    public static Assembly Assembly => typeof(EmbeddedFfmpeg).Assembly;
}
