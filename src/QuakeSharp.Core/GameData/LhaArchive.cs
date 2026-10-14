using System.Buffers.Binary;
using System.Text;

namespace QuakeSharp.Core.GameData;

/// <summary>
/// An LHA archive as the shareware's <c>resource.1</c> is one (design.md §11.10, decision 78): level 0 and 1 headers,
/// stored (<c>-lh0-</c>) and LH5 (<c>-lh5-</c>, 8 KB window) members, each checked by its CRC-16. A self-extracting
/// stub before the first header (the shareware's LHA SFX) is skipped. Read only, from memory; QuakeSharp's own code, as
/// decision 72 asks of its decoders.
/// </summary>
public sealed class LhaArchive
{
    private readonly byte[] data;

    /// <summary>A member: its name as stored (DOS's backslashes kept), how it is packed, where its data starts.</summary>
    public sealed record Entry(string Name, string Method, int Offset, int PackedSize, int Size, ushort Crc);

    public LhaArchive(byte[] data)
    {
        this.data = data;
        Entries = ReadEntries();
    }

    public IReadOnlyList<Entry> Entries { get; }

    /// <summary>The member named <paramref name="name"/> (without regard to case, / or \), or null.</summary>
    public Entry? Find(string name)
    {
        string wanted = name.Replace('\\', '/');
        return Entries.FirstOrDefault(e => string.Equals(e.Name.Replace('\\', '/'), wanted, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A member's contents, unpacked and checked (an <see cref="InvalidDataException"/> if they do not check).</summary>
    public byte[] Extract(Entry entry)
    {
        ReadOnlySpan<byte> packed = data.AsSpan(entry.Offset, entry.PackedSize);
        byte[] output = entry.Method switch
        {
            "-lh0-" => packed.ToArray(),
            "-lh5-" => Lh5.Decode(packed, entry.Size),
            _ => throw new InvalidDataException($"{entry.Name}: LHA method {entry.Method} is not supported"),
        };
        if (Crc16(output) != entry.Crc)
        {
            throw new InvalidDataException($"{entry.Name}: CRC does not match");
        }

        return output;
    }

    /// <summary>LHA's CRC-16 (polynomial 0xA001, reflected, starting at 0).</summary>
    public static ushort Crc16(ReadOnlySpan<byte> bytes)
    {
        int crc = 0;
        foreach (byte b in bytes)
        {
            crc ^= b;
            for (int i = 0; i < 8; i++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xA001 : crc >> 1;
            }
        }

        return (ushort)crc;
    }

    private List<Entry> ReadEntries()
    {
        var entries = new List<Entry>();
        int at = FirstHeader();
        while (at >= 0 && at + 22 <= data.Length && data[at] != 0)
        {
            int headerSize = data[at];
            string method = Encoding.ASCII.GetString(data, at + 2, 5);
            int packed = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(at + 7));
            int size = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(at + 11));
            int level = data[at + 20];
            int nameLength = data[at + 21];
            if (level > 1 || at + 22 + nameLength + 2 > data.Length)
            {
                throw new InvalidDataException($"LHA header level {level} is not supported");
            }

            string name = Encoding.Latin1.GetString(data, at + 22, nameLength);
            ushort crc = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(at + 22 + nameLength));
            int dataStart = at + 2 + headerSize;
            if (level == 1)
            {
                // the extended headers follow the base header and count in the packed size
                int extended = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(dataStart - 2));
                while (extended != 0)
                {
                    packed -= extended;
                    dataStart += extended;
                    extended = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(dataStart - 2));
                }
            }

            if (packed < 0 || dataStart + packed > data.Length)
            {
                throw new InvalidDataException($"{name}: the archive ends inside it");
            }

            entries.Add(new Entry(name, method, dataStart, packed, size, crc));
            at = dataStart + packed;
        }

        return entries;
    }

    // the first header: "-lh?-" two bytes into it, with a header size that fits (past a self-extractor's stub)
    private int FirstHeader()
    {
        for (int i = 0; i + 7 <= data.Length; i++)
        {
            if (data[i + 2] == '-' && data[i + 3] == 'l' && data[i + 4] == 'h' && data[i + 6] == '-' && data[i] >= 22)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// LH5 (Okumura's ar002): blocks of Huffman-coded literals and match lengths (510 codes) and match distances (14
    /// codes, of up to 8 KB), the code lengths themselves coded; matches copied from the 8 KB window.
    /// </summary>
    private ref struct Lh5
    {
        private const int Nc = 510, Np = 14, Nt = 19, Tbit = 5, Cbit = 9, Pbit = 4;
        private const int Threshold = 3, DictSize = 1 << 13;

        private readonly ReadOnlySpan<byte> input;
        private int inputAt;
        private uint bitBuffer;
        private int bitCount;
        private int blockSize;

        private readonly byte[] cLen = new byte[Nc];
        private readonly ushort[] cTable = new ushort[4096];
        private readonly byte[] ptLen = new byte[Nt];
        private readonly ushort[] ptTable = new ushort[256];
        private readonly ushort[] left = new ushort[(2 * Nc) - 1];
        private readonly ushort[] right = new ushort[(2 * Nc) - 1];

        private Lh5(ReadOnlySpan<byte> input)
        {
            this.input = input;
            bitBuffer = 0;
            bitCount = 0;
            Fill();
        }

        public static byte[] Decode(ReadOnlySpan<byte> input, int size)
        {
            var d = new Lh5(input);
            byte[] output = new byte[size];
            int at = 0;
            while (at < size)
            {
                int c = d.DecodeC();
                if (c < 256)
                {
                    output[at++] = (byte)c;
                    continue;
                }

                int length = c - 256 + Threshold;
                int from = at - d.DecodeP() - 1;
                if (from < 0)
                {
                    throw new InvalidDataException("LH5: a match before the start");
                }

                for (int i = 0; i < length && at < size; i++)
                {
                    output[at++] = output[from + i];
                }
            }

            return output;
        }

        // the next n bits, most significant first, without taking them
        private readonly int Peek(int n) => (int)(bitBuffer >> (32 - n));

        private void Skip(int n)
        {
            bitBuffer <<= n;
            bitCount -= n;
            Fill();
        }

        private int Bits(int n)
        {
            if (n == 0)
            {
                return 0;
            }

            int v = Peek(n);
            Skip(n);
            return v;
        }

        // the bit buffer kept at least 24 bits full (zeros past the input's end)
        private void Fill()
        {
            while (bitCount <= 24)
            {
                uint b = inputAt < input.Length ? input[inputAt] : 0u;
                inputAt++;
                bitBuffer |= b << (24 - bitCount);
                bitCount += 8;
            }
        }

        private int DecodeC()
        {
            if (blockSize == 0)
            {
                blockSize = Bits(16);
                ReadPtLen(Nt, Tbit, 3);
                ReadCLen();
                ReadPtLen(Np, Pbit, -1);
            }

            blockSize--;
            int j = cTable[Peek(12)];
            if (j >= Nc)
            {
                uint mask = 1u << (31 - 12);
                do
                {
                    j = (bitBuffer & mask) != 0 ? right[j] : left[j];
                    mask >>= 1;
                }
                while (j >= Nc);
            }

            Skip(cLen[j]);
            return j;
        }

        private int DecodeP()
        {
            int j = ptTable[Peek(8)];
            if (j >= Np)
            {
                uint mask = 1u << (31 - 8);
                do
                {
                    j = (bitBuffer & mask) != 0 ? right[j] : left[j];
                    mask >>= 1;
                }
                while (j >= Np);
            }

            Skip(ptLen[j]);
            return j == 0 ? 0 : (1 << (j - 1)) + Bits(j - 1);
        }

        private void ReadPtLen(int nn, int nbit, int special)
        {
            int n = Bits(nbit);
            if (n == 0)
            {
                int c = Bits(nbit);
                Array.Clear(ptLen, 0, nn);
                Array.Fill(ptTable, (ushort)c);
                return;
            }

            int i = 0;
            while (i < n && i < Nt)
            {
                int c = Peek(3);
                if (c == 7)
                {
                    uint mask = 1u << (31 - 3);
                    while ((bitBuffer & mask) != 0)
                    {
                        mask >>= 1;
                        c++;
                    }
                }

                Skip(c < 7 ? 3 : c - 3);
                ptLen[i++] = (byte)c;
                if (i == special)
                {
                    int zeros = Bits(2);
                    while (--zeros >= 0 && i < Nt)
                    {
                        ptLen[i++] = 0;
                    }
                }
            }

            while (i < nn)
            {
                ptLen[i++] = 0;
            }

            MakeTable(nn, ptLen, 8, ptTable);
        }

        private void ReadCLen()
        {
            int n = Bits(Cbit);
            if (n == 0)
            {
                int c = Bits(Cbit);
                Array.Clear(cLen);
                Array.Fill(cTable, (ushort)c);
                return;
            }

            int i = 0;
            while (i < n && i < Nc)
            {
                int c = ptTable[Peek(8)];
                if (c >= Nt)
                {
                    uint mask = 1u << (31 - 8);
                    do
                    {
                        c = (bitBuffer & mask) != 0 ? right[c] : left[c];
                        mask >>= 1;
                    }
                    while (c >= Nt);
                }

                Skip(ptLen[c]);
                if (c <= 2)
                {
                    int zeros = c == 0 ? 1 : c == 1 ? Bits(4) + 3 : Bits(Cbit) + 20;
                    while (--zeros >= 0 && i < Nc)
                    {
                        cLen[i++] = 0;
                    }
                }
                else
                {
                    cLen[i++] = (byte)(c - 2);
                }
            }

            while (i < Nc)
            {
                cLen[i++] = 0;
            }

            MakeTable(Nc, cLen, 12, cTable);
        }

        // ar002's make_table: a lookup of tableBits bits, codes longer than that continued in a tree (left/right)
        private readonly void MakeTable(int nchar, byte[] bitLen, int tableBits, ushort[] table)
        {
            Span<ushort> count = stackalloc ushort[17];
            Span<ushort> weight = stackalloc ushort[17];
            Span<ushort> start = stackalloc ushort[18];
            for (int i = 0; i < nchar; i++)
            {
                if (bitLen[i] > 16)
                {
                    throw new InvalidDataException("LH5: a code longer than 16 bits");
                }

                count[bitLen[i]]++;
            }

            start[1] = 0;
            for (int i = 1; i <= 16; i++)
            {
                start[i + 1] = (ushort)(start[i] + (count[i] << (16 - i)));
            }

            if (start[17] != 0)
            {
                throw new InvalidDataException("LH5: a bad code table");
            }

            int jutBits = 16 - tableBits;
            for (int i = 1; i <= tableBits; i++)
            {
                start[i] >>= jutBits;
                weight[i] = (ushort)(1 << (tableBits - i));
            }

            for (int i = tableBits + 1; i <= 16; i++)
            {
                weight[i] = (ushort)(1 << (16 - i));
            }

            int k = start[tableBits + 1] >> jutBits;
            if (k != 0)
            {
                int end = 1 << tableBits;
                while (k != end)
                {
                    table[k++] = 0;
                }
            }

            int avail = nchar;
            int mask = 1 << (15 - tableBits);
            for (int ch = 0; ch < nchar; ch++)
            {
                int len = bitLen[ch];
                if (len == 0)
                {
                    continue;
                }

                int nextCode = start[len] + weight[len];
                if (len <= tableBits)
                {
                    for (int i = start[len]; i < nextCode; i++)
                    {
                        table[i] = (ushort)ch;
                    }
                }
                else
                {
                    // the code's first tableBits bits index the table, its rest walks the tree
                    int code = start[len];
                    ref ushort p = ref table[code >> jutBits];
                    int nodeIs = 0; // 0 the table's entry, 1 a left, 2 a right
                    int node = 0;
                    for (int i = len - tableBits; i > 0; i--)
                    {
                        ushort current = nodeIs == 0 ? p : nodeIs == 1 ? left[node] : right[node];
                        if (current == 0)
                        {
                            right[avail] = left[avail] = 0;
                            current = (ushort)avail++;
                            if (nodeIs == 0)
                            {
                                p = current;
                            }
                            else if (nodeIs == 1)
                            {
                                left[node] = current;
                            }
                            else
                            {
                                right[node] = current;
                            }
                        }

                        node = current;
                        nodeIs = (code & mask) != 0 ? 2 : 1;
                        code <<= 1;
                    }

                    if (nodeIs == 1)
                    {
                        left[node] = (ushort)ch;
                    }
                    else
                    {
                        right[node] = (ushort)ch;
                    }
                }

                start[len] = (ushort)nextCode;
            }
        }
    }
}
