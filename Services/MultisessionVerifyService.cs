using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using DiscForgeStudio.Models;

namespace DiscForgeStudio.Services;

public sealed class MultisessionVerifyService
{
    private static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DiscForge Studio", "Multisession");
    private static string SnapshotPath => Path.Combine(Folder, "LastMultisessionSnapshot.json");

    public async Task<MultisessionSnapshot> CreateSnapshotAsync(string drive, long freeBytesBefore, IReadOnlyList<string> previousFiles,
        IReadOnlyList<CompilationItem> newItems, bool finalized, IProgress<(int Percent,string Message)> progress, CancellationToken token)
    {
        var files = new List<MultisessionSnapshotFile>();
        for (var i=0;i<newItems.Count;i++)
        {
            token.ThrowIfCancellationRequested(); var item=newItems[i];
            if(!File.Exists(item.FullPath)) throw new FileNotFoundException("Quelldatei fehlt.", item.FullPath);
            progress.Report((2,$"Multisession-Snapshot {i+1}/{newItems.Count}: {item.RelativePath}"));
            var info=new FileInfo(item.FullPath); files.Add(new(item.FullPath,item.RelativePath,info.Length,await HashAsync(item.FullPath,token)));
        }
        return new(ApplicationVersionService.Current,DateTime.Now,drive,freeBytesBefore,previousFiles.ToList(),files,finalized);
    }

    public void Save(MultisessionSnapshot snapshot)
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllText(SnapshotPath,JsonSerializer.Serialize(snapshot,new JsonSerializerOptions{WriteIndented=true}));
    }

    public MultisessionSnapshot? Load()
    {
        if(!File.Exists(SnapshotPath)) return null;
        try{return JsonSerializer.Deserialize<MultisessionSnapshot>(File.ReadAllText(SnapshotPath));}catch{return null;}
    }

    public async Task<MultisessionVerifyResult> VerifyAsync(string driveName, MultisessionSnapshot snapshot, long freeBytesAfter,
        IProgress<(int Percent,string Message)> progress, CancellationToken token)
    {
        var started=DateTime.Now; var root=driveName.EndsWith('\\')?driveName:driveName+"\\";
        if(!Directory.Exists(root)) throw new IOException($"Die Disc in {root} ist noch nicht lesbar.");
        var fileResults=new List<MultisessionVerifyFile>();
        for(var i=0;i<snapshot.NewFiles.Count;i++)
        {
            token.ThrowIfCancellationRequested(); var expected=snapshot.NewFiles[i];
            var relative=expected.RelativePath.Replace('/',Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);
            var discPath=Path.Combine(root,relative); progress.Report((10+(int)Math.Round((i+1)*75d/Math.Max(1,snapshot.NewFiles.Count)),$"Prüfe neue Datei {i+1}/{snapshot.NewFiles.Count}: {relative}"));
            if(!File.Exists(discPath)){fileResults.Add(new(relative,"Auf Disc fehlt",expected.Sha256,"",expected.Size,0));continue;}
            var size=new FileInfo(discPath).Length;
            if(size!=expected.Size){fileResults.Add(new(relative,"Größe abweichend",expected.Sha256,"",expected.Size,size));continue;}
            var hash=await HashAsync(discPath,token); fileResults.Add(new(relative,hash==expected.Sha256?"OK":"SHA-256 abweichend",expected.Sha256,hash,expected.Size,size));
        }
        progress.Report((90,"Kontrolliere Dateien früherer Sitzungen..."));
        var visible=new HashSet<string>(Directory.EnumerateFiles(root,"*",SearchOption.AllDirectories).Select(path=>path[root.Length..].Replace('/','\\')),StringComparer.OrdinalIgnoreCase);
        var missing=snapshot.PreviousFiles.Where(path=>!visible.Contains(path.Replace('/','\\'))).ToList();
        var matching=fileResults.Count(file=>file.Status=="OK"); var failed=fileResults.Count-matching;
        progress.Report((100,failed==0&&missing.Count==0?"Multisession Verify+ bestanden.":"Multisession Verify+ fand Abweichungen."));
        return new(started,DateTime.Now,snapshot.Created,root,fileResults.Count,matching,failed,missing.Count,snapshot.FreeBytesBefore,freeBytesAfter,fileResults,missing);
    }

    private static async Task<string> HashAsync(string path,CancellationToken token)
    {
        await using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read,1024*1024,FileOptions.Asynchronous|FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream,token));
    }
}
