using System;
using System.IO;
using System.Reflection;

namespace TOAHEX
{
    /// <summary>
    /// PS2 → 3DS 存档转换器。
    /// 由 convert_ps2_to_3ds.py 逐行移植，输出与 Python 版逐字节一致。
    /// 自包含实现（不引用 SaveOffsets / ChecksumHelper），全部常量内嵌。
    /// </summary>
    public static class Ps2To3dsConverter
    {
        /// <summary>PS2 主存档大小（0xBFC8）</summary>
        public const int Ps2MainSize = 49096;   // 0xBFC8
        /// <summary>PS2 系统存档大小（0x728）</summary>
        public const int Ps2SysSize = 1832;     // 0x728
        /// <summary>3DS 主存档大小（0xBFE0）</summary>
        public const int DsMainSize = 49120;    // 0xBFE0
        /// <summary>3DS 系统存档大小（0x744）</summary>
        public const int DsSysSize = 1860;      // 0x744

        /// <summary>
        /// 尾部 B 段（0xB50C..0xB800 = 字段/摄像机参考区）映射分段：
        /// (3DS 起, 3DS 止, delta)，PS2 源偏移 = 3DS 偏移 - delta。
        /// 实测（本 PS2 档 ↔ TotA15 四份原生 3DS 档）该区整体 Δ=+24，但中间夹一段 3DS 专属插入：
        ///   3DS 0xB530/0xB534 恒为 0x01010101×2（四档一致，PS2 无对应）→ 保留 native；
        ///   其后 0xB538..0xB5D0 窗口 Δ 变为 +32（PS2 侧多出的 0xB5B0..0xB5B8 两个 dword 被丢弃）。
        /// 分段后与原生档逐 dword 吻合度 174/189（朴素 +24 仅 151/189），
        /// 且玩家位置副本恰好落在原生档的 0xB568/0xB574(X)、0xB57C(Z)。
        /// </summary>
        private static readonly (int Start, int End, int Delta)[] TailFieldSegments =
        {
            (0xB50C, 0xB530, 24),   // → PS2 0xB4F4..0xB518
            (0xB538, 0xB5D0, 32),   // → PS2 0xB518..0xB5B0
            (0xB5D0, 0xB800, 24)    // → PS2 0xB5B8..0xB7E8
        };

        /// <summary>3DS 专属插入区（0xB530..0xB538，无 PS2 对应）→ 整段保留 3DS(native)。</summary>
        private const int TailDsOnlyInsertStart = 0xB530;
        private const int TailDsOnlyInsertEnd = 0xB538;

        /// <summary>
        /// 4×120B 队伍编成预设块（3DS 10384..10864 = runtime+10084）。
        /// 实测两版结构完全同构、文件 Δ 与主体一致(+4)；唯一差异是每条预设的 16 字节"预设名"
        /// 文字编码（PS2 Shift-JIS ↔ 本地化 3DS ASCII）→ 名称保留 3DS(native) 以免乱码，
        /// 其余字节从 PS2 带入以保留玩家自定义编成。
        /// 若目标 3DS 为日版（名称同为 Shift-JIS），把 <see cref="PresetKeepNameFromTemplate"/> 设为 false。
        /// </summary>
        private const int PresetRecordBase = 10384;
        private const int PresetRecordSize = 120;
        private const int PresetRecordCount = 4;
        private const int PresetNameSize = 16;
        private const bool PresetKeepNameFromTemplate = true;

        /// <summary>
        /// 136B + 168B 块（3DS 10864..11168 = runtime+10564 / +10700）
        /// 复核（2026-10-05 修正）：**并非 3DS 专有** —— PS2 打包 sub_37D5C0 由 obj+10540..10796
        /// 写入 PS2 文件 10868..11172，PS2 加载 sub_37BDF0 亦恢复同区，是双方 1:1 真字段块
        /// （文件 Δ 与主体一致 -4；源偏移 obj+10540↔rt+10564、obj+10676↔rt+10700，源 Δ=+24）。
        /// => 由主体 +4 复制带入即可，不做 native 覆盖（旧版仅覆盖首 4 字节 11000 同样多余）。
        /// 注：本 PS2 档该区全为 0；TotA15 三份中后期 3DS 档在 168B 块内为同一组参考值
        /// (0x2B00..0x2B0F)，早期档为 0 —— 属 3DS 端状态差异，非结构性差异。
        /// </summary>
        private const int PresetBlockRegion = 10864;   // 仅作文档记录，当前无需特判

        /// <summary>主存档头部长度（0x214），写入头部字段 @0x0C</summary>
        private const int HeaderSize = 0x214;

        /// <summary>
        /// 尾部 A 段结束（0xB50C）。0xB2C8..0xB50C 为运行时结构：3DS 侧密集存放 3DS 堆指针
        /// （0x0048xxxx / 0x0879xxxx），PS2 侧同槽位是索引/小整数，两版数据模型不同，
        /// 只能保留 3DS(native) 模板值。
        /// </summary>
        private const int TailPointerArrayEnd = 0xB50C;

        /// <summary>
        /// 尾部 B 段结束（0xB800）。0xB50C..0xB800 为字段/摄像机参考区（PS2 侧 0xB4F4..0xB7E8），
        /// 两版同构、整体 Δ=+24（实测 PS2 0xB600/0xB700/0xB7A0 ↔ 3DS 0xB618/0xB718/0xB7B8 逐 dword 全等），
        /// 含玩家/摄像机参考坐标。旧版整段回写模板值是"摄像机坐标不对应"的根因。
        /// </summary>
        private const int TailFieldRegionEnd = 0xB800;

        /// <summary>
        /// 平台指针带（0x00100000..0x09000000），同时覆盖 3DS 主存/堆指针（0x0048xxxx / 0x0879xxxx）
        /// 与 PS2 主存指针（0x001xxxxx）。落在该带内的槽视为"平台指针槽"，两侧地址不可迁移。
        /// </summary>
        // 平台指针带（收窄，避免误判）：
        //   PS2 EE 主存 0x00100000..0x00200000；3DS 主堆 0x00400000..0x00A00000；线性堆 0x08000000..0x0A000000。
        // 旧版用 0x00100000..0x09000000 一条宽带，把 0xB5F0/0xB5F4 两个普通参数误判为指针。
        private static readonly uint[,] PointerBands =
        {
            { 0x00100000, 0x00200000 },
            { 0x00400000, 0x00A00000 },
            { 0x08000000, 0x0A000000 }
        };

        private static bool IsPlatformPointer(uint value)
        {
            for (int i = 0; i < PointerBands.GetLength(0); i++)
            {
                if (value >= PointerBands[i, 0] && value < PointerBands[i, 1])
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// 尾部字段区（0xB50C..0xB800）指针槽保护：
        /// 任一侧落在指针带内（模板侧 = 3DS 指针；PS2 源侧 = PS2 指针），
        /// 该槽一律还原为 3DS(native) 模板值，避免把 PS2 地址写进 3DS 指针槽；
        /// 其余槽位保留来自 PS2 的数据（分段复制已按对应 delta 带入）。
        /// </summary>
        private static void RestorePointerSlots(byte[] dst, byte[] template, byte[] ps2Src,
                                                int start, int end, int delta)
        {
            for (int off = start; off < end; off += 4)
            {
                if (IsPlatformPointer(BitConverter.ToUInt32(template, off)) ||
                    IsPlatformPointer(BitConverter.ToUInt32(ps2Src, off - delta)))
                {
                    Buffer.BlockCopy(template, off, dst, off, 4);
                }
            }
        }

        /// <summary>
        /// 设置区（116 字节 = runtime+0..115）：9 个动作键位（u32 hid 掩码）+ 7 项设置 + 5 个音量 + 2 个开关。
        /// 3DS 证据链（IDA）：
        ///   读档 sub_3A7C24: sub_30FA1C(off_4F0500, file+0xA9B8, 116)（0x214+0xA9B8 = 0xABCC）
        ///   存盘 sub_37C948: sub_30FA1C(file+0xABCC, off_4F0500, 116)
        ///   默认值 sub_294814: [A,B,Y,X,R,Left,Right,L,Down] + byte[0x24]=(x&amp;0xF8)|7、byte[0x25]=5、
        ///                     byte[0x26]=0x1F、byte[0x29..0x2D]=0x7F、byte[0x2E]=1
        /// PS2 侧同区域是 **8 个 u8 键位 + 6 项设置**（byte[0x24]=6，bit0 关闭），结构与长度均不同。
        /// 旧版：主档前 36B 写死 TOA_000 的玩家自定义键位（B/Y/A 轮换、左右对调），
        ///       其余 80B 从 PS2 搬入 → "按 A"触发的是第 3 个动作而非"决定"，小游戏按键整体错位。
        /// 现改为整段写 3DS 默认设置（主档 0xABCC = 模板默认值，系统档 0x610 = TOASYS 默认值）。
        /// </summary>
        private static readonly byte[] DsMainSettings = HexToBytes(
            "010000000200000000080000000400000001000020000000100000000002000080000000" +
            "07021f00007f7f7f7f7f0100" + new string('0', 68 * 2));

        /// <summary>系统档设置区（与主档同构，实测仅 +0x25/+0x28 两字节不同）。</summary>
        private static readonly byte[] DsSysSettings = HexToBytes(
            "010000000200000000080000000400000001000020000000100000000002000080000000" +
            "07011f00017f7f7f7f7f0100" + new string('0', 68 * 2));


        private static byte[] HexToBytes(string hex)
        {
            if ((hex.Length & 1) != 0)
            {
                throw new ArgumentException("十六进制串长度必须为偶数。");
            }
            byte[] result = new byte[hex.Length / 2];
            for (int i = 0; i < result.Length; i++)
            {
                result[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            }
            return result;
        }

        /// <summary>脚本变量区基址（= SCRIPT_VAR_ENTRY_BASE）。</summary>
        private const int ScriptVarBase = 0x2BA1;

        /// <summary>
        /// 按键/UI 参数表：3DS 启动时 sub_35565C() 用 sub_30AF94(id, keybit) 写入
        /// "变量区 + 8*id"（+0 = 0x200 tag，+4 = keybit）。id 402..425 是按钮位掩码，
        /// 426 = -1 哨兵。实测原生 3DS 档这 25 项与 sub_35565C 常量逐项一致；
        /// PS2 存档里这些槽位是脏数据（A=0xFFFFF9FF、Start=0、B=502），
        /// 原样搬会让按钮映射整表失效 → 按任意键都被解析成 A、Start 无效。
        /// </summary>
        private static readonly (int Id, uint KeyBit)[] UiParamDefaults =
        {
            (402, 0x40), (403, 0x80), (404, 0x20), (405, 0x10),
            (406, 0x400), (407, 0x2), (408, 0x800), (409, 0x1),
            (410, 0x200), (411, 0x0), (412, 0x0), (413, 0x100),
            (414, 0x0), (415, 0x0), (416, 0x8), (417, 0x4),
            (418, 0x10000000), (419, 0x20000000), (420, 0x80000000), (421, 0x40000000),
            (422, 0x0), (423, 0x0), (424, 0x0), (425, 0x0), (426, 0xFFFFFFFF)
        };

        /// <summary>
        /// 从程序集内嵌资源加载 3DS 主存档模板。
        /// 清单名 = RootNamespace(Resources 目录路径)文件名 = TOAHEX.Resources.3ds_template.bin。
        /// </summary>
        public static byte[] LoadEmbeddedTemplate()
        {
            Stream stream = Assembly.GetExecutingAssembly()
                .GetManifestResourceStream("TOAHEX.Resources.3ds_template.bin");
            if (stream == null)
            {
                throw new InvalidOperationException(
                    "找不到内嵌资源 TOAHEX.Resources.3ds_template.bin，请确认项目已将其作为 EmbeddedResource 嵌入。");
            }
            using (stream)
            using (MemoryStream ms = new MemoryStream())
            {
                stream.CopyTo(ms);
                byte[] data = ms.ToArray();
                if (data.Length != DsMainSize)
                {
                    throw new InvalidOperationException(string.Format(
                        "内嵌 3DS 模板大小错误：期望 {0} 字节，实际 {1} 字节。", DsMainSize, data.Length));
                }
                return data;
            }
        }

        /// <summary>
        /// 将 PS2 主存档转换为 3DS 主存档。映射顺序与 Python convert_main 完全一致。
        /// </summary>
        public static byte[] ConvertMain(byte[] ps2Main, byte[] template, int slot = 0)
        {
            if (ps2Main == null)
            {
                throw new ArgumentException("PS2 主存档数据不能为 null。");
            }
            if (template == null)
            {
                throw new ArgumentException("3DS 模板数据不能为 null。");
            }
            if (ps2Main.Length != Ps2MainSize)
            {
                throw new ArgumentException(string.Format(
                    "PS2 主存档大小错误：期望 {0} 字节，实际 {1} 字节。", Ps2MainSize, ps2Main.Length));
            }
            if (template.Length != DsMainSize)
            {
                throw new ArgumentException(string.Format(
                    "3DS 模板大小错误：期望 {0} 字节，实际 {1} 字节。", DsMainSize, template.Length));
            }

            // 以 3DS 模板克隆为底稿：native 覆盖区之外未被映射的区域天然保留模板值
            byte[] dst = (byte[])template.Clone();

            // 同位区：头部标识区（PS2 0..1320 → 3DS 0..1320，两版同偏移直接复制）
            Buffer.BlockCopy(ps2Main, 0, dst, 0, 1320);

            // 头部 0x18/0x1C 两个 f32：3DS 存盘 sub_37C948 只写 0x00..0x10 与 0x14(校验)，
            // 从不写这两处（原生四档恒为 0）；PS2 存盘会写（疑似存档缩略图相机参数）→ 保留模板值。
            Buffer.BlockCopy(template, 0x18, dst, 0x18, 8);

            // 显示块 0x24 = 存档槽号（3DS 存盘 sub_37C948 的 `v6[9] = a2`，原生 TOA_000..003 = 0..3）。
            Buffer.BlockCopy(BitConverter.GetBytes(slot & 0xF), 0, dst, 0x24, 4);

            // +4 区：主体存档区（PS2 1324..43984 → 3DS 1320..43980，PS2 相对 3DS 整体多 4 字节偏移）
            Buffer.BlockCopy(ps2Main, 1324, dst, 1320, 42660);

            // 设置区 43980..44096（0xABCC..0xAC40，116B）：整段写 3DS 默认设置。
            // 旧版只写前 36B 的玩家自定义键位、其余 80B 从 PS2 搬（PS2 为 8×u8 键位 + 6 项设置）→ 结构错位。
            Buffer.BlockCopy(DsMainSettings, 0, dst, 43980, 116);

            // 按键/UI 参数表：PS2 档这些槽位是脏数据，按 3DS 默认表（sub_35565C 常量）重建。
            foreach (var (id, keyBit) in UiParamDefaults)
            {
                int off = ScriptVarBase + id * 8;
                Buffer.BlockCopy(BitConverter.GetBytes(0x200u), 0, dst, off, 4);
                Buffer.BlockCopy(BitConverter.GetBytes(keyBit), 0, dst, off + 4, 4);
            }


            // -24 区：尾部区（PS2 44072..49096 → 3DS 44096..49120，两档尾部均到达文件末尾）
            Buffer.BlockCopy(ps2Main, 44072, dst, 44096, Ps2MainSize - 44072);

            // -4 微调区：地图/坐标字段（PS2 相对 3DS 少 4 字节：0x52C→0x528、0x530→0x52C、0x534→0x530）
            Buffer.BlockCopy(ps2Main, 0x52C, dst, 0x528, 4);
            Buffer.BlockCopy(ps2Main, 0x530, dst, 0x52C, 4);
            Buffer.BlockCopy(ps2Main, 0x534, dst, 0x530, 0x10);

            // native 覆盖区：尾部 A 段 0xB2C8..0xB50C
            // 3DS 此处为运行时结构（密集 3DS 堆指针 0x0048xxxx / 0x0879xxxx），PS2 同槽位为索引/小整数。
            // 从 PS2 复制会把 PS2 地址写进 3DS 指针槽 → 必须回写 3DS（native）模板值。
            Buffer.BlockCopy(template, 0xB2C8, dst, 0xB2C8, TailPointerArrayEnd - 0xB2C8);

            // 尾部 B 段 0xB50C..0xB800（字段/摄像机参考区，PS2 源区 0xB4F4..0xB7E8）：
            // 两版同构但 Δ 分段（见 TailFieldSegments），含玩家/摄像机参考坐标（玩家 X/Z 在此区重现）。
            // 旧版把整段回写成模板值是"玩家坐标对、摄像机坐标不对"的根因。
            foreach (var (segStart, segEnd, delta) in TailFieldSegments)
            {
                Buffer.BlockCopy(ps2Main, segStart - delta, dst, segStart, segEnd - segStart);
                RestorePointerSlots(dst, template, ps2Main, segStart, segEnd, delta);
            }

            // 3DS 专属插入区（0xB530..0xB538）整段保留 3DS(native)
            Buffer.BlockCopy(template, TailDsOnlyInsertStart, dst, TailDsOnlyInsertStart,
                             TailDsOnlyInsertEnd - TailDsOnlyInsertStart);

            // 0x2284：原"若与模板不一致则强制回写模板"的浮点特判已删除（2026-10-05 复核）：
            //   3DS 0x2284 = PS2 0x2288 = 800.0（两版恒为常量 0x44480000），主体 +4 复制已正确落位，
            //   该特判在实测四档中恒为 no-op。

            // 4×120B 队伍编成预设块：非名称部分从 PS2 带入，名称字段保留 3DS(native) 以免编码乱码。
            for (int k = 0; k < PresetRecordCount; k++)
            {
                int rec = PresetRecordBase + PresetRecordSize * k;
                Buffer.BlockCopy(ps2Main, rec + PresetNameSize + 4, dst,
                                 rec + PresetNameSize, PresetRecordSize - PresetNameSize);
                if (PresetKeepNameFromTemplate)
                {
                    Buffer.BlockCopy(template, rec, dst, rec, PresetNameSize);
                }
            }

            // 136B+168B 块（3DS 10864..11168）为双方 1:1 真字段块（PS2 sub_37D5C0 存 / sub_37BDF0 取），
            // 已由上方主体 +4 复制正确带入，故不再做 native 覆盖。

            // 头部字段：@0x0C = 头部长度 0x214；@0x10 = 头部校验和；@0x14 = 主体校验和
            PutU32(dst, 0x0C, 0x214);
            PutU32(dst, 0x10, WordSum(dst, 0x20, 0x1F4));
            PutU32(dst, 0x14, WordSum(dst, 0x214, DsMainSize - 0x214));

            return dst;
        }

        /// <summary>
        /// 将 PS2 系统存档转换为 3DS 系统存档。映射顺序与 Python convert_system 完全一致。
        /// </summary>
        public static byte[] ConvertSystem(byte[] ps2Sys)
        {
            if (ps2Sys == null)
            {
                throw new ArgumentException("PS2 系统存档数据不能为 null。");
            }
            if (ps2Sys.Length != Ps2SysSize)
            {
                throw new ArgumentException(string.Format(
                    "PS2 系统存档大小错误：期望 {0} 字节，实际 {1} 字节。", Ps2SysSize, ps2Sys.Length));
            }

            // 3DS 系统档以零填充新建（系统档无模板底稿）
            byte[] dst = new byte[DsSysSize];

            // 同位区：系统数据前段（PS2 0..1552 → 3DS 0..1552）
            Buffer.BlockCopy(ps2Sys, 0, dst, 0, 1552);

            // 设置区 1552..1668（0x610..0x684，116B）：与主档 0xABCC 同构，整段写 3DS 系统档默认设置。
            // 旧版只写 36B（且漏了 0x10=L 一项）并把 PS2 的 6 项设置头带进来 → 键位表注册错位。
            Buffer.BlockCopy(DsSysSettings, 0, dst, 1552, 116);

            // +28 区：系统数据后段（PS2 1640..1704 → 3DS 1668..1732）
            Buffer.BlockCopy(ps2Sys, 1640, dst, 1668, 64);

            // +28 区：尾部（PS2 1704..1832 → 3DS 1732..1860，两档尾部均到达文件末尾）
            Buffer.BlockCopy(ps2Sys, 1704, dst, 1732, Ps2SysSize - 1704);

            // 头部字段：@0x00 = 全档校验和（数据区 8..1860 的小端 u32 累加和）
            PutU32(dst, 0, WordSum(dst, 8, DsSysSize - 8));

            return dst;
        }

        /// <summary>
        /// 校验 3DS 主存档：长度、头部校验和（@0x10）与主体校验和（@0x14）。
        /// </summary>
        public static void VerifyMain(byte[] dsMain)
        {
            if (dsMain == null)
            {
                throw new InvalidOperationException("3DS 主存档数据不能为 null。");
            }
            if (dsMain.Length != DsMainSize)
            {
                throw new InvalidOperationException(string.Format(
                    "3DS 主存档长度错误：期望 {0} 字节，实际 {1} 字节。", DsMainSize, dsMain.Length));
            }
            if (BitConverter.ToUInt32(dsMain, 0x10) != WordSum(dsMain, 0x20, 0x1F4))
            {
                throw new InvalidOperationException("3DS 主存档头部校验和（@0x10）不匹配。");
            }
            if (BitConverter.ToUInt32(dsMain, 0x14) != WordSum(dsMain, 0x214, DsMainSize - 0x214))
            {
                throw new InvalidOperationException("3DS 主存档主体校验和（@0x14）不匹配。");
            }
        }

        /// <summary>
        /// 校验 3DS 系统存档：长度与 @0x00 全档校验和。
        /// </summary>
        public static void VerifySystem(byte[] dsSys)
        {
            if (dsSys == null)
            {
                throw new InvalidOperationException("3DS 系统存档数据不能为 null。");
            }
            if (dsSys.Length != DsSysSize)
            {
                throw new InvalidOperationException(string.Format(
                    "3DS 系统存档长度错误：期望 {0} 字节，实际 {1} 字节。", DsSysSize, dsSys.Length));
            }
            if (BitConverter.ToUInt32(dsSys, 0) != WordSum(dsSys, 8, DsSysSize - 8))
            {
                throw new InvalidOperationException("3DS 系统存档校验和（@0x00）不匹配。");
            }
        }

        /// <summary>
        /// 小端 u32 累加和：对 [offset, offset+count) 每 4 字节取小端 u32 累加并回绕（& 0xFFFFFFFF）。
        /// 与 Python word_sum / ChecksumHelper.WordSum 语义一致（局部实现，避免并行任务冲突）。
        /// </summary>
        private static uint WordSum(byte[] data, int offset, int count)
        {
            uint sum = 0;
            int end = offset + count;
            for (int pos = offset; pos < end; pos += 4)
            {
                sum += BitConverter.ToUInt32(data, pos);
            }
            return sum;
        }

        /// <summary>
        /// 以小端字节序写入 u32。
        /// </summary>
        private static void PutU32(byte[] data, int offset, uint value)
        {
            data[offset] = (byte)(value & 0xFF);
            data[offset + 1] = (byte)((value >> 8) & 0xFF);
            data[offset + 2] = (byte)((value >> 16) & 0xFF);
            data[offset + 3] = (byte)((value >> 24) & 0xFF);
        }
    }
}
