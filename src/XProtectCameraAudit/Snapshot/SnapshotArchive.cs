using System;
using System.IO;
using System.Linq;
using XProtectCameraAudit.Model;

namespace XProtectCameraAudit.Snapshot
{
    /// <summary>
    /// Each camera's last good thumbnail, kept between runs. The recordings only reach
    /// back as far as the retention time: a camera broken for longer has no frame left
    /// to fetch, and nobody remembers where it pointed. Run the tool regularly and the
    /// archive keeps its last view, shown on the card with the date it was taken.
    ///
    /// One file per camera, "camera GUID.jpg"; its last-write time is the frame's time.
    /// A new frame replaces the archived one only when it is newer, so an old recorded
    /// frame fetched after a breakdown never pushes out a later one.
    /// </summary>
    public sealed class SnapshotArchive
    {
        readonly string _directory;

        public SnapshotArchive(string directory)
        {
            _directory = directory;
        }

        /// <summary>Stores this run's frames, then gives cameras without one their archived view.</summary>
        public void Apply(SiteAudit site)
        {
            Directory.CreateDirectory(_directory);
            int saved = 0, shown = 0;
            foreach (var cam in site.Recorders.SelectMany(r => r.Hardwares).SelectMany(h => h.Cameras))
            {
                if (cam.CameraGuid == Guid.Empty) continue;
                var snap = cam.Snapshot;
                string path = Path.Combine(_directory, cam.CameraGuid.ToString("D") + ".jpg");

                try
                {
                    if (snap.ThumbnailBase64 != null && snap.FrameTimestampUtc.HasValue)
                    {
                        if (!File.Exists(path) || File.GetLastWriteTimeUtc(path) < snap.FrameTimestampUtc.Value)
                        {
                            File.WriteAllBytes(path, Convert.FromBase64String(snap.ThumbnailBase64));
                            File.SetLastWriteTimeUtc(path, snap.FrameTimestampUtc.Value);
                            saved++;
                        }
                    }
                    else if (File.Exists(path))
                    {
                        snap.ArchivedThumbnailBase64 = Convert.ToBase64String(File.ReadAllBytes(path));
                        snap.ArchivedFrameUtc = File.GetLastWriteTimeUtc(path);
                        shown++;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine("  archive: {0}: {1}", cam.Name, ex.Message);
                }
            }
            Console.WriteLine("Snapshot archive {0}: {1} updated, {2} camera(s) without a frame shown from it.",
                _directory, saved, shown);
        }
    }
}
