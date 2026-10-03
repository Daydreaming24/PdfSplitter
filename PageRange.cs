using System;

namespace PdfPageSplitter
{
    /// <summary>
    /// 主页"分片范围"两个输入框的纯解析逻辑，不依赖任何控件，便于单独推敲和测试。
    ///
    /// 约定：用户可以随意输入，解析时就地纠正而不是报错——
    /// 负数和 0 归到第 1 页，超出总页数的归到最后一页，起止写反了自动对调。
    /// 只有"根本不是整数"（空、小数、字母）才判为无效。
    /// </summary>
    internal static class PageRange
    {
        /// <summary>
        /// 解析单个页码。<paramref name="totalPages"/> 为 null 表示还没读出总页数，
        /// 此时只做下限约束。
        /// </summary>
        /// <returns>文本是合法整数时返回 true，并输出已钳制到 [1, 总页数] 的页码。</returns>
        public static bool TryRead(string text, int? totalPages, out int page)
        {
            text = (text ?? "").Trim();
            page = 0;
            if (text.Length == 0) return false;
            bool negative = text[0] == '-';
            int offset = negative || text[0] == '+' ? 1 : 0;
            if (offset == text.Length) return false;
            int maximum = totalPages ?? Int32.MaxValue;
            // Saturate while parsing so even pasted integers beyond Int64 can be corrected.
            for (int i = offset; i < text.Length; i++)
            {
                if (text[i] < '0' || text[i] > '9') return false;
                page = (int)Math.Min(maximum, (long)page * 10 + text[i] - '0');
            }
            page = negative ? 1 : Math.Max(1, page);
            return true;
        }

        /// <summary>
        /// 校验一对起止页。两端都合法时按从小到大输出。
        /// </summary>
        /// <returns>空字符串表示通过；否则为给用户看的提示语。</returns>
        public static string Validate(string startText, string endText, int? totalPages, out int first, out int last)
        {
            first = last = 0;
            if (!TryRead(startText, totalPages, out first) || !TryRead(endText, totalPages, out last))
                return "请填写起始页和结束页，页码须为整数。";
            if (first > last) Swap(ref first, ref last);
            return "";
        }

        public static void Swap(ref int first, ref int last)
        {
            int swap = first;
            first = last;
            last = swap;
        }
    }
}
