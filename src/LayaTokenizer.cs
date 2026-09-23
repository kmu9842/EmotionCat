using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace EmotionCat
{
    /// Gemma-style BPE for Laya multilingual. Exact port of tools/onnx/tokenizer_pack.py (Reference).
    public sealed class LayaTokenizer
    {
        private const char Space = '▁';
        private readonly Dictionary<string, int> vocab = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly Dictionary<long, long> merges = new Dictionary<long, long>();  // (left<<32|right) -> (rank<<32|merged)
        private readonly KeyValuePair<string, int>[] added;
        private readonly HashSet<char> addedStarts = new HashSet<char>();
        private readonly int[] byteTokens = new int[256];

        public LayaTokenizer(string path)
        {
            using (var reader = new BinaryReader(File.OpenRead(path), Encoding.UTF8))
            {
                if (Encoding.ASCII.GetString(reader.ReadBytes(5)) != "ECTK1") throw new InvalidDataException("tokenizer.bin 형식이 올바르지 않습니다.");
                int count = reader.ReadInt32();
                for (int id = 0; id < count; id++)
                {
                    string token = Encoding.UTF8.GetString(reader.ReadBytes(reader.ReadUInt16()));
                    if (token.Length > 0) vocab[token] = id;
                }
                int mergeCount = reader.ReadInt32();
                for (int rank = 0; rank < mergeCount; rank++)
                {
                    long left = reader.ReadInt32(), right = reader.ReadInt32(), merged = reader.ReadInt32();
                    long key = (left << 32) | (uint)right;
                    merges[key] = ((long)rank << 32) | (uint)merged;  // later duplicates win, as in the reference
                }
                int addedCount = reader.ReadInt32();
                var list = new List<KeyValuePair<string, int>>();
                for (int i = 0; i < addedCount; i++)
                {
                    int id = reader.ReadInt32();
                    string content = Encoding.UTF8.GetString(reader.ReadBytes(reader.ReadUInt16()));
                    if (content.Length == 0) continue;
                    vocab[content] = id;
                    list.Add(new KeyValuePair<string, int>(content, id));
                    addedStarts.Add(content[0]);
                }
                added = list.OrderByDescending(a => a.Key.Length).ToArray();  // stable: ties keep file order
            }
            // Characters with their own token (e.g. tab) have no byte-fallback token; HF maps a missing one to <unk>.
            int unknown = vocab["<unk>"];
            for (int b = 0; b < 256; b++)
            {
                int id;
                byteTokens[b] = vocab.TryGetValue("<0x" + b.ToString("X2") + ">", out id) ? id : unknown;
            }
        }

        public List<int> Encode(string text)
        {
            var output = new List<int>();
            int start = 0, i = 0;
            while (i < text.Length)
            {
                KeyValuePair<string, int>? hit = null;
                if (addedStarts.Contains(text[i]))
                    foreach (var token in added)
                        if (i + token.Key.Length <= text.Length && String.CompareOrdinal(text, i, token.Key, 0, token.Key.Length) == 0) { hit = token; break; }
                if (hit.HasValue)
                {
                    if (i > start) Segment(text.Substring(start, i - start), output);
                    output.Add(hit.Value.Value);
                    i += hit.Value.Key.Length;
                    start = i;
                }
                else i++;
            }
            if (start < text.Length) Segment(text.Substring(start), output);
            return output;
        }

        private void Segment(string text, List<int> output)
        {
            text = text.Replace(' ', Space);
            if (text.Length == 0 || text[0] != Space) text = Space + text;
            int start = 0;
            for (int i = 1; i <= text.Length; i++)
            {
                if (i == text.Length || text[i] == Space)
                {
                    Piece(text.Substring(start, i - start), output);
                    start = i;
                }
            }
        }

        private void Piece(string text, List<int> output)
        {
            var ids = new List<int>();
            for (int i = 0; i < text.Length; i++)
            {
                string symbol = Char.IsSurrogatePair(text, i) ? text.Substring(i++, 2) : text[i].ToString();
                int id;
                if (vocab.TryGetValue(symbol, out id)) ids.Add(id);
                else foreach (byte b in Encoding.UTF8.GetBytes(symbol)) ids.Add(byteTokens[b]);
            }
            while (ids.Count > 1)
            {
                long bestRank = long.MaxValue; int bestPos = -1; int bestMerged = 0;
                for (int p = 0; p < ids.Count - 1; p++)
                {
                    long hit;
                    if (merges.TryGetValue(((long)ids[p] << 32) | (uint)ids[p + 1], out hit) && (hit >> 32) < bestRank)
                    {
                        bestRank = hit >> 32; bestPos = p; bestMerged = (int)(hit & 0xFFFFFFFF);
                    }
                }
                if (bestPos < 0) break;
                ids[bestPos] = bestMerged;
                ids.RemoveAt(bestPos + 1);
            }
            output.AddRange(ids);
        }
    }
}
