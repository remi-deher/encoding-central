
namespace Encoding.Contracts.Libraries;

public sealed record AssignLibraryFlowRequest(Guid? FlowId,int? Version,int ExpectedRevision);
public sealed record LibraryFlowAssignment(Guid FlowId,int Version,int Revision,string Name);
public sealed record LibraryMediaFile(Guid Id,string RelativePath,long Bytes,DateTimeOffset ModifiedAt);
public sealed record LibraryScanStatus(Guid Id,string State,string Detail,DateTimeOffset? CompletedAt);
public sealed record LibraryWorkflowResponse(LibraryFlowAssignment? Assignment,LibraryScanStatus? Scan,IReadOnlyList<LibraryMediaFile> Files,int Revision,
    int TotalFiles=0,int Limit=10000,int Offset=0);
public sealed record EnqueueLibraryFilesRequest(IReadOnlyList<Guid> FileIds,int AssignmentRevision);
public sealed record LibraryScanLease(Guid Id,Guid LibraryId,long Epoch,string Root,string MediaType);
public sealed record DiscoveredLibraryFile(string RelativePath,long Bytes,DateTimeOffset ModifiedAt);
public sealed record CompleteLibraryScan(long Epoch,IReadOnlyList<DiscoveredLibraryFile> Files,string? Error);
public sealed record LibraryScanFence(long Epoch);

public static class LibraryMediaTypes
{
    public static bool Matches(string path,string type)
    {
        var extension=Path.GetExtension(path).ToLowerInvariant();
        return type switch {
            "video"=>new[]{".mkv",".mp4",".m4v",".avi",".mov",".webm",".ts",".m2ts",".mpg",".mpeg"}.Contains(extension),
            "music"=>new[]{".mp3",".flac",".m4a",".aac",".ogg",".opus",".wav",".wma",".aiff"}.Contains(extension),
            "image"=>new[]{".jpg",".jpeg",".png",".webp",".gif",".tiff",".tif",".bmp",".avif",".heic"}.Contains(extension),
            "subtitles"=>new[]{".srt",".ass",".ssa",".vtt",".sub"}.Contains(extension),
            _=>false
        };
    }
}

public sealed record LibraryNodeProcessingLimit(Guid NodeId,int MaxConcurrentTasks);
public sealed record LibraryProcessingSettings(bool WatchEnabled,int ScanIntervalSeconds,int? MaxConcurrentTasks,IReadOnlyList<LibraryNodeProcessingLimit> NodeLimits,int Revision);
public sealed record SaveLibraryProcessingSettings(bool WatchEnabled,int ScanIntervalSeconds,int? MaxConcurrentTasks,IReadOnlyList<LibraryNodeProcessingLimit> NodeLimits,int ExpectedRevision);
