using System.Text;

namespace BluetoothHeartrateModule
{
    internal class Converter
    {
        internal static string FormatAsMac(ulong deviceMac)
        {
            var bits = BitConverter.GetBytes(deviceMac);
            Array.Reverse(bits);
            return BitConverter.ToString(bits, 2, 6).Replace('-', ':');
        }

        internal static int[] ToDigitArray(int num, int totalWidth)
        {
            return num.ToString().PadLeft(totalWidth, '0').Select(digit => int.Parse(digit.ToString())).ToArray();
        }

        internal static byte[] GetAsciiStringInt(int number)
        {
            return Encoding.ASCII.GetBytes(number.ToString());
        }
    }
}
