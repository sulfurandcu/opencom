using OpenCom.Wpf.Models;

namespace OpenCom.Wpf.Services;

/// <summary>
/// 触发匹配引擎。
/// 匹配状态机只在 RX 数据到达时调用；行内容改动后 Rebuild 重建编译缓存
/// （丢弃在途半匹配）。
/// </summary>
public sealed class TriggerEngine
{
    private sealed class MatchState
    {
        public readonly byte[] Tail = new byte[MaxPattern];
        public int TailLen;
    }

    public const int MaxPattern = 1024;
    private const int MaxTx = 8192;
    private const int RxChunk = 4096;

    private readonly List<MatchState> _states = new();

    /// <summary>重新编译所有行（行改动后调用）。</summary>
    public void Rebuild(IList<SendRow> rows)
    {
        foreach (var r in rows)
        {
            r.TxBytes = CompileTx(r, out var ok);
            if (!ok)
            {
                r.TxBytes = Array.Empty<byte>();
                r.Pattern = Array.Empty<byte>();
                continue;
            }
            r.Pattern = r.Role == RowRole.Match ? ParsePattern(r) : Array.Empty<byte>();
        }
        while (_states.Count < rows.Count) _states.Add(new MatchState());
        ResetStates();   // 行改动：丢弃在途半匹配
    }

    /// <summary>行边界：清掉跨行 carry。匹配只在完整的一行内进行
    /// （一行收完喂一次、喂完即重置，跨行的数据拼不成匹配）。</summary>
    public void ResetStates()
    {
        foreach (var s in _states) s.TailLen = 0;
    }

    /// <summary>喂入 RX 数据；每个命中回调一次（发送目标行内容）。</summary>
    public void Feed(IList<SendRow> rows, byte[] data, Action<SendRow> onHit)
    {
        for (int i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            if (r.Role != RowRole.Match || r.Pattern.Length == 0) continue;
            int t = r.TargetIndex;
            if (t < 0 || t >= rows.Count) continue;
            var target = rows[t];
            if (target.TxBytes.Length == 0) continue;

            if (i >= _states.Count) _states.Add(new MatchState());
            int hits = MatchStep(_states[i], r.Pattern, data, r.MatchMode);
            for (int h = 0; h < hits; h++)
            {
                r.MatchCount++;
                onHit(target);
            }
        }
    }

    public static bool TryCompileContent(SendRow row)
    {
        var bytes = CompileTx(row, out var ok);
        if (ok)
        {
            row.TxBytes = bytes;
            /* 匹配行的模式也要同步刷新：否则内容编辑后引擎还在用旧模式
             * 匹配（"匹配行内容修改后不触发、重启才生效"的根因）。 */
            if (row.Role == RowRole.Match)
                row.Pattern = ParsePattern(row);
        }
        return ok;
    }

    private static byte[] CompileTx(SendRow r, out bool ok)
    {
        if (r.Format == RowFormat.Hex)
        {
            var n = ParseHex(r.Content, out var bytes);
            ok = n >= 0 && bytes.Length <= MaxTx;
            return ok ? bytes : Array.Empty<byte>();
        }
        var b = System.Text.Encoding.UTF8.GetBytes(r.Content);
        ok = b.Length <= MaxTx;
        return ok ? b : Array.Empty<byte>();
    }

    private static byte[] ParsePattern(SendRow r)
    {
        if (r.Format == RowFormat.Hex)
        {
            var n = ParseHex(r.Content, out var bytes);
            return n >= 0 && bytes.Length <= MaxPattern ? bytes : Array.Empty<byte>();
        }
        var b = System.Text.Encoding.UTF8.GetBytes(r.Content);
        return b.Length <= MaxPattern ? b : Array.Empty<byte>();
    }

    /// <summary>HEX 文本解析："31 32,33;A4-0B" 均可；单字符放高半字节（如 "A" = 0xA0）。</summary>
    private static int ParseHex(string text, out byte[] bytes)
    {
        var list = new List<byte>();
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            if (c is ' ' or '\t' or ',' or ';' or '\r' or '\n' or '-') { i++; continue; }
            int hi = HexVal(c);
            if (hi < 0) { bytes = Array.Empty<byte>(); return -1; }
            i++;
            int lo = 0;
            if (i < text.Length && HexVal(text[i]) >= 0) lo = HexVal(text[i++]);
            list.Add((byte)((hi << 4) | lo));
        }
        bytes = list.ToArray();
        return bytes.Length;
    }

    private static int HexVal(char c) =>
        c switch
        {
            >= '0' and <= '9' => c - '0',
            >= 'a' and <= 'f' => c - 'a' + 10,
            >= 'A' and <= 'F' => c - 'A' + 10,
            _ => -1
        };

    // ---- 三种匹配模式的状态机 ----

    private static int MatchStep(MatchState ms, byte[] pat, byte[] data, MatchMode mode)
    {
        int plen = pat.Length, len = data.Length, hits = 0;
        if (plen <= 0 || len <= 0) return 0;

        switch (mode)
        {
            case MatchMode.Contains:
            {
                // tail(≤plen-1) + 新块拼接后朴素扫描，结尾回存 plen-1 字节
                var buf = new byte[MaxPattern + RxChunk];
                int total = Math.Min(ms.TailLen + len, buf.Length);
                Array.Copy(ms.Tail, 0, buf, 0, ms.TailLen);
                Array.Copy(data, 0, buf, ms.TailLen, total - ms.TailLen);
                for (int i = 0; i + plen <= total; i++)
                    if (buf.AsSpan(i, plen).SequenceEqual(pat)) hits++;
                int keep = Math.Min(total, plen - 1);
                Array.Copy(buf, total - keep, ms.Tail, 0, keep);
                ms.TailLen = keep;
                break;
            }
            case MatchMode.Equals:
            {
                // 行级匹配下的"完全相等"：整行与模式完全相等才算命中。
                // 一行喂一次（见 MainViewModel.FeedLine），行内不滑窗——
                // 否则触发回复（模式+附加字节）会在行内偏移处再次命中，
                // 两个触发端互相回发形成环路。
                if (len == plen && data.AsSpan(0, len).SequenceEqual(pat)) hits = 1;
                break;
            }
            case MatchMode.Prefix:
            {
                // 前缀状态机：追加字节，不等则丢首字节重比，满 plen 全等即命中
                for (int i = 0; i < len; i++)
                {
                    if (ms.TailLen < plen && pat[ms.TailLen] == data[i])
                    {
                        ms.Tail[ms.TailLen++] = data[i];
                        if (ms.TailLen == plen)
                        {
                            hits++;
                            ms.TailLen = 0;
                        }
                        continue;
                    }
                    // 失配：追加后收缩到最长合法前缀（KMP 退化版）
                    if (ms.TailLen < MaxPattern) ms.Tail[ms.TailLen++] = data[i];
                    else
                    {
                        Array.Copy(ms.Tail, 1, ms.Tail, 0, MaxPattern - 1);
                        ms.Tail[MaxPattern - 1] = data[i];
                    }
                    while (ms.TailLen > 0)
                    {
                        if (ms.Tail.AsSpan(0, ms.TailLen).SequenceEqual(pat.AsSpan(0, ms.TailLen))) break;
                        Array.Copy(ms.Tail, 1, ms.Tail, 0, ms.TailLen - 1);
                        ms.TailLen--;
                    }
                }
                break;
            }
        }
        return hits;
    }
}
