using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace DupCleaner
{
    /// <summary>扫描到的媒体文件</summary>
    public class MediaFile
    {
        public string Path;
        public string Name;
        public long Size;
        public DateTime Created;
    }

    /// <summary>一组判定为重复的文件；Keep 保留，Delete 建议删除</summary>
    public class DupGroup
    {
        public long Size;
        public MediaFile Keep;
        public List<MediaFile> Delete = new List<MediaFile>();
    }

    public static class Scanner
    {
        // 小于该大小的文件不处理（界面可改，默认 100KB）
        public static long MinFileSize = 100 * 1024;

        /// <summary>递归枚举 root 下所有文件绝对路径（失败目录静默跳过；记录已访问目录防 junction 死循环）</summary>
        private static IEnumerable<string> EnumerateMedia(string root)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Stack<string> stack = new Stack<string>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                string dir = stack.Pop();
                string full;
                try { full = Path.GetFullPath(dir).TrimEnd('\\', '/'); }
                catch { continue; }
                if (!seen.Add(full)) continue; // 遇到链接环/重复目录直接跳过
                string[] files = null;
                string[] subdirs = null;
                try
                {
                    files = Directory.GetFiles(dir);
                    subdirs = Directory.GetDirectories(dir);
                }
                catch { continue; }
                foreach (string d in subdirs) stack.Push(d);
                foreach (string f in files) yield return f;
            }
        }

        private static MediaFile Build(string path)
        {
            FileInfo fi = new FileInfo(path);
            MediaFile mf = new MediaFile();
            mf.Path = path;
            mf.Name = fi.Name;
            mf.Size = fi.Length;
            mf.Created = fi.CreationTime;
            return mf;
        }

        /// <summary>递归扫描 dirs，逐文件回调进度 (current,total,fileName)</summary>
        public static List<MediaFile> ScanAll(List<string> dirs, Action<int, int, string> cb)
        {
            int total = 0;
            foreach (string d in dirs)
            {
                try { foreach (string f in EnumerateMedia(d)) total++; } catch { }
            }

            List<MediaFile> list = new List<MediaFile>();
            int cur = 0;
            foreach (string d in dirs)
            {
                IEnumerable<string> enu = null;
                try { enu = EnumerateMedia(d); } catch { continue; }
                if (enu == null) continue;
                foreach (string f in enu)
                {
                    cur++;
                    MediaFile mf = null;
                    try { mf = Build(f); } catch { }
                    if (mf != null && mf.Size >= MinFileSize) list.Add(mf);
                    if (cb != null) cb(cur, total, f);
                }
            }
            return list;
        }

        /// <summary>读取文件前 32KB、中点 32KB 与末 32KB 作为指纹；文件不足 96KB（3 段）则读取全部内容</summary>
        private static byte[] ReadFingerprint(string path, long size)
        {
            const int chunk = 32 * 1024; // 32KB
            using (FileStream f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                // 不足三段采样（96KB）：读全文件比对
                if (size <= (long)chunk * 3)
                {
                    byte[] whole = new byte[size];
                    int r = ReadAll(f, whole, 0, (int)size);
                    Array.Resize(ref whole, r);
                    return whole;
                }

                byte[] head = new byte[chunk];
                int h = ReadAll(f, head, 0, chunk);

                f.Seek(size - chunk, SeekOrigin.Begin);
                byte[] tail = new byte[chunk];
                int t = ReadAll(f, tail, 0, chunk);

                // 中点采样（此时 size>3*chunk，中段必然可落在不重叠区间）
                long midStart = (size - chunk) / 2;
                if (midStart < chunk) midStart = chunk;
                f.Seek(midStart, SeekOrigin.Begin);
                byte[] mid = new byte[chunk];
                int m = ReadAll(f, mid, 0, chunk);

                byte[] buf = new byte[h + m + t];
                Buffer.BlockCopy(head, 0, buf, 0, h);
                Buffer.BlockCopy(mid, 0, buf, h, m);
                Buffer.BlockCopy(tail, 0, buf, h + m, t);
                return buf;
            }
        }

        private static int ReadAll(Stream s, byte[] b, int off, int len)
        {
            int total = 0;
            while (total < len)
            {
                int r = s.Read(b, off + total, len - total);
                if (r <= 0) break;
                total += r;
            }
            return total;
        }

        /// <summary>对已读到的多点采样指纹做内存哈希作为分组键</summary>
        private static string FingerprintKey(long size, byte[] fp)
        {
            using (SHA1 sha = SHA1.Create())
            {
                byte[] h = sha.ComputeHash(fp);
                return size.ToString(CultureInfo.InvariantCulture) + "|" + Convert.ToBase64String(h);
            }
        }

        /// <summary>判定重复：先按大小分组，同大小再按首尾 4KB 指纹分组。回调 (done,total)</summary>
        public static List<DupGroup> Detect(List<MediaFile> list, Action<int, int> cb)
        {
            Dictionary<long, List<MediaFile>> bySize = new Dictionary<long, List<MediaFile>>();
            foreach (MediaFile f in list)
            {
                List<MediaFile> b;
                if (!bySize.TryGetValue(f.Size, out b))
                {
                    b = new List<MediaFile>();
                    bySize[f.Size] = b;
                }
                b.Add(f);
            }

            // 只有同一大小出现 >=2 次才可能是重复
            List<MediaFile> candidates = new List<MediaFile>();
            foreach (KeyValuePair<long, List<MediaFile>> kv in bySize)
                if (kv.Value.Count >= 2) candidates.AddRange(kv.Value);

            int total = candidates.Count;
            string[] keys = new string[total];
            int[] done = new int[1];
            object lockObj = new object();

            Parallel.For(0, total, i =>
            {
                MediaFile f = candidates[i];
                string k = null;
                try
                {
                    byte[] fp = ReadFingerprint(f.Path, f.Size);
                    k = FingerprintKey(f.Size, fp);
                }
                catch { }
                lock (lockObj)
                {
                    keys[i] = k;
                    done[0]++;
                    if (cb != null) cb(done[0], total);
                }
            });

            Dictionary<string, List<MediaFile>> byKey = new Dictionary<string, List<MediaFile>>();
            for (int i = 0; i < total; i++)
            {
                if (string.IsNullOrEmpty(keys[i])) continue;
                List<MediaFile> g;
                if (!byKey.TryGetValue(keys[i], out g))
                {
                    g = new List<MediaFile>();
                    byKey[keys[i]] = g;
                }
                g.Add(candidates[i]);
            }

            List<DupGroup> groups = new List<DupGroup>();
            foreach (KeyValuePair<string, List<MediaFile>> kv in byKey)
            {
                List<MediaFile> same = kv.Value;
                if (same.Count < 2) continue;
                // 创建时间最早保留；相同时按路径排序保证确定
                List<MediaFile> sorted = same.OrderBy(x => x.Created.Ticks)
                                            .ThenBy(x => x.Path, StringComparer.Ordinal).ToList();
                DupGroup g = new DupGroup();
                g.Size = sorted[0].Size;
                g.Keep = sorted[0];
                for (int i = 1; i < sorted.Count; i++) g.Delete.Add(sorted[i]);
                groups.Add(g);
            }

            groups.Sort(delegate(DupGroup a, DupGroup b) { return b.Size.CompareTo(a.Size); });
            return groups;
        }

        public static string FormatSize(long b)
        {
            if (b >= 1024L * 1024 * 1024)
                return (b / 1024.0 / 1024 / 1024).ToString("0.00") + " GB";
            if (b >= 1024L * 1024)
                return (b / 1024.0 / 1024).ToString("0.0") + " MB";
            return (b / 1024.0).ToString("0") + " KB";
        }

        /// <summary>解析 ktv_config.json，返回 mediaDirs 列表</summary>
        public static List<string> LoadKtvConfig(string path)
        {
            List<string> res = new List<string>();
            string txt = File.ReadAllText(path);
            try
            {
                using (JsonDocument doc = JsonDocument.Parse(txt))
                {
                    JsonElement root = doc.RootElement;
                    if (root.ValueKind == JsonValueKind.Object)
                    {
                        JsonElement arr;
                        if (root.TryGetProperty("mediaDirs", out arr) && arr.ValueKind == JsonValueKind.Array)
                        {
                            foreach (JsonElement el in arr.EnumerateArray())
                            {
                                if (el.ValueKind != JsonValueKind.String) continue;
                                string s = el.GetString().Trim();
                                if (s.Length > 0) res.Add(s);
                            }
                        }
                    }
                }
            }
            catch { /* 走下方正则兜底 */ }

            // 兜底：JSON 结构不符合预期时，用正则提取所有形如 D:\xxx 的绝对路径
            if (res.Count == 0)
            {
                var m = System.Text.RegularExpressions.Regex.Matches(txt,
                    "\"([A-Za-z]:[\\\\/][^\"]+)\"",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                foreach (System.Text.RegularExpressions.Match mm in m)
                {
                    string s = mm.Groups[1].Value.Trim();
                    if (s.Length > 0 && !res.Contains(s)) res.Add(s);
                }
            }
            return res;
        }

        /// <summary>移到回收站（可恢复）。全部成功返回 true；失败则 failureCount>0。progress(done,total) 可选</summary>
        public static int DeleteToRecycleBin(List<string> paths, Action<int, int> progress = null)
        {
            int fail = 0;
            int total = paths.Count;
            for (int i = 0; i < total; i++)
            {
                try
                {
                    Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(
                        paths[i], Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                        Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                }
                catch
                {
                    if (File.Exists(paths[i])) fail++;
                }
                progress?.Invoke(i + 1, total);
            }
            return fail;
        }

        /// <summary>永久删除。返回失败数量。progress(done,total) 可选</summary>
        public static int DeletePermanent(List<string> paths, Action<int, int> progress = null)
        {
            int fail = 0;
            int total = paths.Count;
            for (int i = 0; i < total; i++)
            {
                string p = paths[i];
                try { if (File.Exists(p)) File.Delete(p); }
                catch { if (File.Exists(p)) fail++; }
                progress?.Invoke(i + 1, total);
            }
            return fail;
        }

        // ==================== 重复文件夹比对 ====================

        /// <summary>扫描到的一个文件夹</summary>
        public class FolderEntry
        {
            public string Path;
            public string Name;
            public int FileCount;
            public long TotalSize;
            public DateTime Created;
        }

        /// <summary>一组判定为重复的文件夹；Keep 保留，Delete 建议删除</summary>
        public class DupFolderGroup
        {
            public FolderEntry Keep;
            public List<FolderEntry> Delete = new List<FolderEntry>();
        }

        private static string NormDir(string d)
        {
            return Path.GetFullPath(d).TrimEnd('\\', '/');
        }

        /// <summary>扫描 roots 下所有子文件夹（不含根自身），逐文件夹回调 (cur,total,dir)。返回每个文件夹的文件数/总大小/创建时间。</summary>
        public static List<FolderEntry> ScanFolders(List<string> roots, Action<int, int, string> cb)
        {
            var rootSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string r in roots)
            {
                if (Directory.Exists(r)) rootSet.Add(NormDir(r));
            }

            // 第一遍：收集全部候选子文件夹（防 junction 死循环）
            var allFolders = new List<string>();
            var seenDir = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string r in roots)
            {
                if (!Directory.Exists(r)) continue;
                Stack<string> stack = new Stack<string>();
                stack.Push(r);
                while (stack.Count > 0)
                {
                    string dir = stack.Pop();
                    string full;
                    try { full = NormDir(dir); } catch { continue; }
                    if (!seenDir.Add(full)) continue;
                    string[] subs = null;
                    try { subs = Directory.GetDirectories(dir); }
                    catch { continue; }
                    foreach (string s in subs) stack.Push(s);
                    if (!rootSet.Contains(full)) allFolders.Add(full); // 根自身不纳入候选
                }
            }

            int total = allFolders.Count;
            var list = new List<FolderEntry>();
            for (int cur = 0; cur < total; cur++)
            {
                string f = allFolders[cur];
                if (cb != null) cb(cur + 1, total, f);
                try
                {
                    var fe = new FolderEntry();
                    fe.Path = f;
                    fe.Name = Path.GetFileName(f.TrimEnd('\\', '/'));
                    fe.Created = Directory.GetCreationTime(f);
                    AggregateStats(f, fe);
                    list.Add(fe);
                }
                catch { }
            }
            return list;
        }

        // 累加文件夹内所有文件的数量与总字节数（含全部文件，不做大小过滤，避免漏删小文件）
        private static void AggregateStats(string dir, FolderEntry fe)
        {
            long sum = 0;
            int cnt = 0;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Stack<string> stack = new Stack<string>();
            stack.Push(dir);
            while (stack.Count > 0)
            {
                string d = stack.Pop();
                string full;
                try { full = NormDir(d); } catch { continue; }
                if (!seen.Add(full)) continue;
                string[] fs = null, ss = null;
                try { fs = Directory.GetFiles(d); ss = Directory.GetDirectories(d); }
                catch { continue; }
                foreach (string s in ss) stack.Push(s);
                foreach (string f in fs)
                {
                    try { sum += new FileInfo(f).Length; cnt++; } catch { }
                }
            }
            fe.TotalSize = sum;
            fe.FileCount = cnt;
        }

        /// <summary>判定重复文件夹：先按(文件数,总大小)分组，组内再比对“相对路径+首尾4KB指纹”全签名。</summary>
        public static List<DupFolderGroup> DetectFolders(List<FolderEntry> folders, Action<int, int> cb)
        {
            // 先按(文件数,总大小)粗分组
            var byStat = new Dictionary<string, List<FolderEntry>>();
            foreach (FolderEntry fe in folders)
            {
                string key = fe.FileCount + "|" + fe.TotalSize;
                List<FolderEntry> l;
                if (!byStat.TryGetValue(key, out l)) { l = new List<FolderEntry>(); byStat[key] = l; }
                l.Add(fe);
            }
            var cand = new List<FolderEntry>();
            foreach (var kv in byStat)
                if (kv.Value.Count >= 2) cand.AddRange(kv.Value);

            // 组内算全签名
            int total = cand.Count;
            string[] sigs = new string[total];
            int[] done = new int[1];
            object lockObj = new object();
            Parallel.For(0, total, i =>
            {
                string s = null;
                try { s = FolderSignature(cand[i].Path); } catch { }
                lock (lockObj)
                {
                    sigs[i] = s;
                    done[0]++;
                    if (cb != null) cb(done[0], total);
                }
            });

            var bySig = new Dictionary<string, List<FolderEntry>>();
            for (int i = 0; i < total; i++)
            {
                if (string.IsNullOrEmpty(sigs[i])) continue;
                List<FolderEntry> l;
                if (!bySig.TryGetValue(sigs[i], out l)) { l = new List<FolderEntry>(); bySig[sigs[i]] = l; }
                l.Add(cand[i]);
            }

            var groups = new List<DupFolderGroup>();
            foreach (var kv in bySig)
            {
                List<FolderEntry> same = kv.Value;
                if (same.Count < 2) continue;
                List<FolderEntry> sorted = same.OrderBy(x => x.Created.Ticks)
                                               .ThenBy(x => x.Path, StringComparer.Ordinal).ToList();
                var g = new DupFolderGroup();
                g.Keep = sorted[0];
                for (int i = 1; i < sorted.Count; i++) g.Delete.Add(sorted[i]);
                groups.Add(g);
            }
            return groups;
        }

        // 文件夹内容全签名：相对路径 + 每个文件的 大小|首尾4KB哈希，整体再哈希一次
        private static string FolderSignature(string dir)
        {
            StringBuilder sb = new StringBuilder();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var stack = new Stack<KeyValuePair<string, string>>(); // dir, relPrefix
            stack.Push(new KeyValuePair<string, string>(dir, ""));
            while (stack.Count > 0)
            {
                var kv = stack.Pop();
                string d = kv.Key;
                string rel = kv.Value;
                string full;
                try { full = NormDir(d); } catch { continue; }
                if (!seen.Add(full)) continue;
                string[] fs = null, ss = null;
                try { fs = Directory.GetFiles(d); ss = Directory.GetDirectories(d); }
                catch { continue; }
                foreach (string s in ss)
                {
                    string rrel = rel.Length == 0 ? Path.GetFileName(s.TrimEnd('\\', '/')) : rel + "/" + Path.GetFileName(s.TrimEnd('\\', '/'));
                    stack.Push(new KeyValuePair<string, string>(s, rrel));
                }
                foreach (string f in fs)
                {
                    string fr = rel.Length == 0 ? Path.GetFileName(f) : rel + "/" + Path.GetFileName(f);
                    try
                    {
                        FileInfo fi = new FileInfo(f);
                        byte[] fp = ReadFingerprint(f, fi.Length);
                        sb.Append(fr).Append('|').Append(FingerprintKey(fi.Length, fp)).Append('\n');
                    }
                    catch { }
                }
            }
            using (SHA1 sha = SHA1.Create())
            {
                return Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString())));
            }
        }

        /// <summary>把整个文件夹移到回收站。返回失败数量。progress(done,total) 可选</summary>
        public static int DeleteFolderToRecycleBin(List<string> dirs, Action<int, int> progress = null)
        {
            int fail = 0;
            int total = dirs.Count;
            for (int i = 0; i < total; i++)
            {
                string p = dirs[i];
                try
                {
                    Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(
                        p, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                        Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin,
                        Microsoft.VisualBasic.FileIO.UICancelOption.DoNothing);
                }
                catch { if (Directory.Exists(p)) fail++; }
                progress?.Invoke(i + 1, total);
            }
            return fail;
        }

        /// <summary>永久删除整个文件夹。返回失败数量。progress(done,total) 可选</summary>
        public static int DeleteFolderPermanent(List<string> dirs, Action<int, int> progress = null)
        {
            int fail = 0;
            int total = dirs.Count;
            for (int i = 0; i < total; i++)
            {
                string p = dirs[i];
                try { if (Directory.Exists(p)) Directory.Delete(p, true); }
                catch { if (Directory.Exists(p)) fail++; }
                progress?.Invoke(i + 1, total);
            }
            return fail;
        }
    }
}