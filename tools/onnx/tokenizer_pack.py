"""Developer-only: pack tokenizer.json into tokenizer.bin, and a pure reference BPE to port natively.

tokenizer.bin (little endian):
  b"ECTK1"
  u32 vocab_count, then per id: u16 byte_len + UTF-8 bytes
  u32 merge_count, then per rank: i32 left_id, i32 right_id, i32 merged_id
  u32 added_count, then per token: i32 id + u16 byte_len + UTF-8 bytes
Algorithm (matches HF tokenizers for this Gemma-style config):
  1. Split text on added tokens (longest match, left to right); added tokens map to their id.
  2. Other segments: replace " " with "▁", prepend "▁" if absent, split before every "▁".
  3. Each piece: chars -> ids (unknown char -> <0xXX> per UTF-8 byte), then repeatedly apply
     the lowest-rank merge (leftmost on ties) until none applies.
"""
import argparse
import json
import struct

SPACE = "▁"


def load(path):
    t = json.load(open(path, encoding="utf-8"))
    vocab = t["model"]["vocab"]
    for a in t["added_tokens"]:
        vocab[a["content"]] = a["id"]
    by_id = [None] * (max(vocab.values()) + 1)
    for s, i in vocab.items():
        by_id[i] = s
    merges = []
    for m in t["model"]["merges"]:
        left, right = m if isinstance(m, list) else m.split(" ", 1)
        merges.append((vocab[left], vocab[right], vocab[left + right]))
    added = [(a["id"], a["content"]) for a in t["added_tokens"]]
    return by_id, merges, added


def write(path, by_id, merges, added):
    with open(path, "wb") as f:
        f.write(b"ECTK1")
        f.write(struct.pack("<I", len(by_id)))
        for s in by_id:
            b = (s or "").encode("utf-8")
            f.write(struct.pack("<H", len(b)) + b)
        f.write(struct.pack("<I", len(merges)))
        for m in merges:
            f.write(struct.pack("<iii", *m))
        f.write(struct.pack("<I", len(added)))
        for i, s in added:
            b = s.encode("utf-8")
            f.write(struct.pack("<iH", i, len(b)) + b)


class Reference:
    def __init__(self, by_id, merges, added):
        self.vocab = {s: i for i, s in enumerate(by_id) if s is not None}
        self.rank = {(l, r): (k, m) for k, (l, r, m) in enumerate(merges)}
        self.added = sorted(added, key=lambda a: -len(a[1]))

    def piece(self, text):
        ids = []
        for ch in text:
            if ch in self.vocab:
                ids.append(self.vocab[ch])
            else:
                ids.extend(self.vocab["<0x%02X>" % b] for b in ch.encode("utf-8"))
        while len(ids) > 1:
            best = None
            for p in range(len(ids) - 1):
                hit = self.rank.get((ids[p], ids[p + 1]))
                if hit and (best is None or hit[0] < best[0]):
                    best = (hit[0], p, hit[1])
            if best is None:
                break
            _, p, m = best
            ids[p:p + 2] = [m]
        return ids

    def segment(self, text):
        text = text.replace(" ", SPACE)
        if not text.startswith(SPACE):
            text = SPACE + text
        out, start = [], 0
        for i in range(1, len(text) + 1):
            if i == len(text) or text[i] == SPACE:
                out.extend(self.piece(text[start:i]))
                start = i
        return out

    def encode(self, text):
        out, i, start = [], 0, 0
        while i < len(text):
            hit = next((a for a in self.added if text.startswith(a[1], i)), None)
            if hit:
                if i > start:
                    out.extend(self.segment(text[start:i]))
                out.append(hit[0])
                i += len(hit[1]); start = i
            else:
                i += 1
        if start < len(text):
            out.extend(self.segment(text[start:]))
        return out


if __name__ == "__main__":
    p = argparse.ArgumentParser()
    p.add_argument("--tokenizer", required=True)
    p.add_argument("--out", required=True)
    a = p.parse_args()
    write(a.out, *load(a.tokenizer))
