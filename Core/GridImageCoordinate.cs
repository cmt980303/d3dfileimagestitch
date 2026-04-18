using System;
using System.IO;
using System.Text.RegularExpressions;

namespace GPUStitch.Core
{
    /// <summary>
    /// 文件名中解析出的图像网格坐标。
    ///
    /// 约定：
    /// - 前三位数字表示行号；
    /// - 后三位数字表示列号；
    /// - 既支持 "020016.jpg"，也支持 "020 016.jpg" 这类带分隔符的形式。
    ///
    /// 例如：
    /// 020016 -> 第 21 行、第 17 列（内部仍按 0 基索引存储，即 Row=20, Column=16）。
    /// </summary>
    public readonly struct GridImageCoordinate : IEquatable<GridImageCoordinate>
    {
        private static readonly Regex FileNamePattern =
            new Regex(@"(?<row>\d{3})\D*(?<col>\d{3})$", RegexOptions.Compiled);

        public GridImageCoordinate(int row, int column)
        {
            Row = row;
            Column = column;
        }

        /// <summary>
        /// 0 基行号。
        /// 用户口头表达中的“第 21 行”对应 Row=20。
        /// </summary>
        public int Row { get; }

        /// <summary>
        /// 0 基列号。
        /// 用户口头表达中的“第 17 列”对应 Column=16。
        /// </summary>
        public int Column { get; }

        public bool Equals(GridImageCoordinate other) =>
            Row == other.Row && Column == other.Column;

        public override bool Equals(object? obj) =>
            obj is GridImageCoordinate other && Equals(other);

        public override int GetHashCode() =>
            HashCode.Combine(Row, Column);

        public override string ToString() =>
            $"({Row}, {Column})";

        public static bool TryParseFromFilePath(string? filePath, out GridImageCoordinate coordinate)
        {
            coordinate = default;

            if (string.IsNullOrWhiteSpace(filePath))
                return false;

            string fileName = Path.GetFileNameWithoutExtension(filePath) ?? string.Empty;
            var match = FileNamePattern.Match(fileName);
            if (!match.Success)
                return false;

            if (!int.TryParse(match.Groups["row"].Value, out int row))
                return false;
            if (!int.TryParse(match.Groups["col"].Value, out int column))
                return false;

            coordinate = new GridImageCoordinate(row, column);
            return true;
        }
    }
}
