using System.Text;

namespace SaveEditor.Core;

/// <summary>
/// C# port of pieroxy's lz-string (Base64 flavor), the compression used by
/// RPG Maker MV .rpgsave files and many HTML5/localStorage games.
/// </summary>
public static class LzString
{
    private const string KeyStrBase64 = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/=";

    public static string CompressToBase64(string input)
    {
        string res = Compress(input, 6, i => KeyStrBase64[i]);
        return (res.Length % 4) switch
        {
            1 => res + "===",
            2 => res + "==",
            3 => res + "=",
            _ => res,
        };
    }

    public static string? DecompressFromBase64(string input)
    {
        if (string.IsNullOrEmpty(input)) return "";
        return Decompress(input.Length, 32, i =>
        {
            int v = KeyStrBase64.IndexOf(input[i]);
            return v < 0 ? throw new SaveFormatException("Invalid lz-string base64 character.") : v;
        });
    }

    private sealed class BitWriter(int bitsPerChar, Func<int, char> getCharFromInt)
    {
        private readonly StringBuilder _data = new();
        private int _val;
        private int _position;

        public void WriteBits(int value, int count)
        {
            for (int i = 0; i < count; i++)
            {
                _val = (_val << 1) | (value & 1);
                if (_position == bitsPerChar - 1)
                {
                    _position = 0;
                    _data.Append(getCharFromInt(_val));
                    _val = 0;
                }
                else
                {
                    _position++;
                }
                value >>= 1;
            }
        }

        public string Finish()
        {
            while (true)
            {
                _val <<= 1;
                if (_position == bitsPerChar - 1)
                {
                    _data.Append(getCharFromInt(_val));
                    break;
                }
                _position++;
            }
            return _data.ToString();
        }
    }

    private static string Compress(string uncompressed, int bitsPerChar, Func<int, char> getCharFromInt)
    {
        var dictionary = new Dictionary<string, int>();
        var dictionaryToCreate = new HashSet<string>();
        string w = "";
        int enlargeIn = 2;
        int dictSize = 3;
        int numBits = 2;
        var writer = new BitWriter(bitsPerChar, getCharFromInt);

        void OutputW()
        {
            if (dictionaryToCreate.Contains(w))
            {
                if (w[0] < 256)
                {
                    writer.WriteBits(0, numBits);
                    writer.WriteBits(w[0], 8);
                }
                else
                {
                    writer.WriteBits(1, numBits);
                    writer.WriteBits(w[0], 16);
                }
                if (--enlargeIn == 0)
                {
                    enlargeIn = 1 << numBits;
                    numBits++;
                }
                dictionaryToCreate.Remove(w);
            }
            else
            {
                writer.WriteBits(dictionary[w], numBits);
            }
            if (--enlargeIn == 0)
            {
                enlargeIn = 1 << numBits;
                numBits++;
            }
        }

        foreach (char ch in uncompressed)
        {
            string c = ch.ToString();
            if (!dictionary.ContainsKey(c))
            {
                dictionary[c] = dictSize++;
                dictionaryToCreate.Add(c);
            }

            string wc = w + c;
            if (dictionary.ContainsKey(wc))
            {
                w = wc;
            }
            else
            {
                OutputW();
                dictionary[wc] = dictSize++;
                w = c;
            }
        }

        if (w != "")
        {
            OutputW();
        }

        writer.WriteBits(2, numBits);
        return writer.Finish();
    }

    private static string? Decompress(int length, int resetValue, Func<int, int> getNextValue)
    {
        var dictionary = new List<string> { "", "", "" };
        int enlargeIn = 4;
        int dictSize = 4;
        int numBits = 3;
        var result = new StringBuilder();

        int dataVal = getNextValue(0);
        int dataPosition = resetValue;
        int dataIndex = 1;

        int ReadBits(int count)
        {
            int bits = 0;
            int maxpower = 1 << count;
            int power = 1;
            while (power != maxpower)
            {
                int resb = dataVal & dataPosition;
                dataPosition >>= 1;
                if (dataPosition == 0)
                {
                    dataPosition = resetValue;
                    dataVal = getNextValue(dataIndex++);
                }
                bits |= (resb > 0 ? 1 : 0) * power;
                power <<= 1;
            }
            return bits;
        }

        string c;
        switch (ReadBits(2))
        {
            case 0: c = ((char)ReadBits(8)).ToString(); break;
            case 1: c = ((char)ReadBits(16)).ToString(); break;
            case 2: return "";
            default: return null;
        }

        dictionary.Add(c);
        string w = c;
        result.Append(c);

        while (true)
        {
            if (dataIndex > length) return "";

            int code = ReadBits(numBits);
            switch (code)
            {
                case 0:
                    dictionary.Add(((char)ReadBits(8)).ToString());
                    code = dictSize++;
                    enlargeIn--;
                    break;
                case 1:
                    dictionary.Add(((char)ReadBits(16)).ToString());
                    code = dictSize++;
                    enlargeIn--;
                    break;
                case 2:
                    return result.ToString();
            }

            if (enlargeIn == 0)
            {
                enlargeIn = 1 << numBits;
                numBits++;
            }

            string entry;
            if (code < dictSize && code < dictionary.Count && (code >= 3 || dictionary[code].Length > 0))
            {
                entry = dictionary[code];
            }
            else if (code == dictSize)
            {
                entry = w + w[0];
            }
            else
            {
                return null;
            }
            result.Append(entry);

            dictionary.Add(w + entry[0]);
            dictSize++;
            enlargeIn--;
            w = entry;

            if (enlargeIn == 0)
            {
                enlargeIn = 1 << numBits;
                numBits++;
            }
        }
    }
}
