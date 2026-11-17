using System;
using System.Runtime.InteropServices;

namespace WallpaperManager
{
    /// <summary>
    /// 极简 Media Foundation 解码：SourceReader 直接谈成 MFVideoFormat_RGB32，
    /// 我们自己把帧拷进 DIB，再交给 GDI 画到桌面层窗口里。
    ///
    /// 为什么不用 WPF MediaElement：微软对 24H2 桌面改版的说明里明确提到，
    /// MPV / VLC 这类自己带渲染器的后端能工作，**WMF（MediaElement）后端不行**；
    /// 而且 MediaElement 也拿不到像素，没法往分层子窗口上画。
    ///
    /// 为什么不用 D3D11 + DirectComposition：那要手写十来个 COM 接口的 vtable，
    /// 一个顺序错就是 AccessViolation。既然窗口已经是普通分层子窗口，
    /// GDI StretchDIBits 就够了（1080p30 大约占半个核的 15%）。
    ///
    /// 接口声明用 [ComImport] 让 CLR 自己生成代理，不手写 vtable；
    /// 用不到的方法只占位、签名无关紧要（x64 下槽位只看顺序）。
    /// </summary>
    static class MfVideo
    {
        const uint MF_VERSION = 0x00020070;                    // MF_VERSION(2, 112)
        const uint MF_SOURCE_READER_FIRST_VIDEO_STREAM = 0xFFFFFFFC;
        const uint MF_SOURCE_READER_ALL_STREAMS = 0xFFFFFFFE;
        const uint MF_SOURCE_READERF_ENDOFSTREAM = 0x2;

        static readonly Guid MF_MT_MAJOR_TYPE = new Guid("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
        static readonly Guid MF_MT_SUBTYPE = new Guid("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
        static readonly Guid MF_MT_FRAME_SIZE = new Guid("1652c33d-d6b2-4012-b834-72030849a37d");
        static readonly Guid MF_MT_DEFAULT_STRIDE = new Guid("644b4e48-1e02-4510-b06b-c102ca1ed88c");
        static readonly Guid MFMediaType_Video = new Guid("73646976-0000-0010-8000-00aa00389b71");
        static readonly Guid MFVideoFormat_RGB32 = new Guid("00000016-0000-0010-8000-00aa00389b71");
        static readonly Guid MFVideoFormat_NV12 = new Guid("3231564e-0000-0010-8000-00aa00389b71");

        // 关键：不开这个开关，SourceReader 只肯吐解码器的原生 NV12，
        // 谈 RGB32 一律返回 0xc00d36b4(MF_E_INVALIDMEDIATYPE)。之前视频一直「打不开」就是它。
        static readonly Guid MF_SOURCE_READER_ENABLE_VIDEO_PROCESSING =
            new Guid("fb394f3d-ccf1-42ee-bbb3-f9b845d5681d");
        static readonly Guid MF_SOURCE_READER_ENABLE_ADVANCED_VIDEO_PROCESSING =
            new Guid("0f81da2c-b537-4672-a8b2-a681b17307a3");

        static bool mfStarted;

        /// <summary>
        /// 11-14：管理器进程会用这个解码器抽视频首帧做「内容指纹」比对。
        /// 抽帧时把 [MF] 诊断日志静音，避免界面刷新把日志刷爆；播放器进程不受影响。
        /// </summary>
        internal static bool Quiet = false;

        [DllImport("mfplat.dll", ExactSpelling = true)]
        static extern int MFStartup(uint version, uint flags);

        [DllImport("mfplat.dll", ExactSpelling = true)]
        static extern int MFShutdown();

        [DllImport("mfplat.dll", ExactSpelling = true)]
        static extern int MFCreateMediaType(out IMFMediaType type);

        [DllImport("mfplat.dll", ExactSpelling = true)]
        static extern int MFCreateAttributes(out IMFAttributes attrs, uint initialSize);

        [DllImport("mfreadwrite.dll", CharSet = CharSet.Unicode)]
        static extern int MFCreateSourceReaderFromURL(string url, [MarshalAs(UnmanagedType.Interface)] IMFAttributes attributes, out IMFSourceReader reader);

        [DllImport("kernel32.dll", EntryPoint = "RtlMoveMemory")]
        static extern void CopyMemory(IntPtr dest, IntPtr src, UIntPtr count);

        // ---------------- COM 接口（顺序即 vtable，不可乱动） ----------------

        [ComImport, Guid("2cd2d921-c447-44a7-a13c-4adabfc247e3"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMFAttributes
        {
            [PreserveSig] int GetItem(ref Guid key, IntPtr value);
            [PreserveSig] int GetItemType(ref Guid key, out int type);
            [PreserveSig] int CompareItem(ref Guid key, IntPtr value, out int result);
            [PreserveSig] int Compare(IntPtr theirs, int matchType, out int result);
            [PreserveSig] int GetUINT32(ref Guid key, out uint value);
            [PreserveSig] int GetUINT64(ref Guid key, out ulong value);
            [PreserveSig] int GetDouble(ref Guid key, out double value);
            [PreserveSig] int GetGUID(ref Guid key, out Guid value);
            [PreserveSig] int GetStringLength(ref Guid key, out uint length);
            [PreserveSig] int GetString(ref Guid key, IntPtr buf, uint size, out uint length);
            [PreserveSig] int GetAllocatedString(ref Guid key, out IntPtr str, out uint length);
            [PreserveSig] int GetBlobSize(ref Guid key, out uint size);
            [PreserveSig] int GetBlob(ref Guid key, IntPtr buf, uint size, out uint blobSize);
            [PreserveSig] int GetAllocatedBlob(ref Guid key, out IntPtr buf, out uint size);
            [PreserveSig] int GetUnknown(ref Guid key, ref Guid iid, out IntPtr ppv);
            [PreserveSig] int SetItem(ref Guid key, IntPtr value);
            [PreserveSig] int DeleteItem(ref Guid key);
            [PreserveSig] int DeleteAllItems();
            [PreserveSig] int SetUINT32(ref Guid key, uint value);
            [PreserveSig] int SetUINT64(ref Guid key, ulong value);
            [PreserveSig] int SetDouble(ref Guid key, double value);
            [PreserveSig] int SetGUID(ref Guid key, ref Guid value);
            [PreserveSig] int SetString(ref Guid key, [MarshalAs(UnmanagedType.LPWStr)] string value);
            [PreserveSig] int SetBlob(ref Guid key, IntPtr buf, uint size);
            [PreserveSig] int SetUnknown(ref Guid key, IntPtr obj);
            [PreserveSig] int LockStore();
            [PreserveSig] int UnlockStore();
            [PreserveSig] int GetCount(out uint count);
            [PreserveSig] int GetItemByIndex(uint index, out Guid key, IntPtr value);
            [PreserveSig] int CopyAllItems(IntPtr dest);
        }

        [ComImport, Guid("44ae0fa8-ea31-4109-8d2e-4cae4997c555"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMFMediaType : IMFAttributes
        {
            [PreserveSig] int GetMajorType(out Guid major);
            [PreserveSig] int IsCompressedFormat(out int compressed);
            [PreserveSig] int IsEqual(IntPtr other, out uint flags);
            [PreserveSig] int GetRepresentation(ref Guid rep, out IntPtr ppv);
            [PreserveSig] int FreeRepresentation(ref Guid rep, IntPtr pv);
        }

        // 注意：IMFSourceReader **不**继承 IMFAttributes。之前写成 : IMFAttributes
        // 会让 CLR 认为前面还有 30 个槽位，SetCurrentMediaType 等调用全部错位，
        // 表现出来就是各种莫名字段的 HRESULT。
        [ComImport, Guid("70ae66f2-c809-4e4f-8915-bdcb406b7993"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMFSourceReader
        {
            [PreserveSig] int GetStreamSelection(uint index, out int selected);
            [PreserveSig] int SetStreamSelection(uint index, int selected);
            [PreserveSig] int GetNativeMediaType(uint index, uint typeIndex, out IMFMediaType type);
            [PreserveSig] int GetCurrentMediaType(uint index, out IMFMediaType type);
            [PreserveSig] int SetCurrentMediaType(uint index, IntPtr reserved, [MarshalAs(UnmanagedType.Interface)] IMFMediaType type);
            [PreserveSig] int SetCurrentPosition(IntPtr timeFormat, ref PROPVARIANT position);
            // 关键：这里必须是 out IntPtr，不能是 out IMFSample。
            // 11-11 11:58 实机日志：ReadSample 明明返回成功（否则会走到「ReadSample 失败」分支），
            // 但 CLR 在把返回指针包装成 IMFSample 时 QI 报 0x80004002 E_NOINTERFACE，
            // 一帧都拿不到。改成裸指针，样本/缓冲的取数据全部走下面手写的 vtable，
            // 彻底不经过 RCW 的接口转换。
            [PreserveSig] int ReadSample(uint index, uint controlFlags,
                                         out uint actualIndex, out uint streamFlags,
                                         out long timestamp, out IntPtr sample);
            [PreserveSig] int Flush(uint index);
            [PreserveSig] int GetServiceForStream(uint index, ref Guid service, ref Guid iid, out IntPtr ppv);
            [PreserveSig] int GetPresentationAttribute(uint index, ref Guid attr, IntPtr value);
        }

        // ---------------- 手写 vtable（不经过 CLR 的接口转换） ----------------
        //
        // 为什么手写：IMFSourceReader 的 ReadSample 返回的 IMFSample 用 [ComImport] 接口接，
        // 本机（.NET Framework + 24H2 的 mfreadwrite）稳定报 E_NOINTERFACE，一帧都拿不到。
        // 裸指针 + 直接按槽位取函数就绕开了 RCW 那一层。
        //
        // !! 槽位一定要算上基接口 !!
        //   IMFSample : IMFAttributes，所以前面先有 IMFAttributes 的 30 个方法：
        //     0..2   IUnknown
        //     3..32  IMFAttributes（GetItem … CopyAllItems，共 30 个）
        //     33 GetSampleFlags   34 SetSampleFlags
        //     35 GetSampleTime    36 SetSampleTime
        //     37 GetSampleDuration 38 SetSampleDuration
        //     39 GetBufferCount   40 GetBufferByIndex
        //     41 ConvertToContiguousBuffer …
        //   第一版按 11 算，调到的是 IMFAttributes::GetStringLength，拿回 0xC00D36E6。
        //
        //   IMFMediaBuffer : IUnknown，不继承 IMFAttributes：
        //     0..2 IUnknown   3 Lock   4 Unlock   5 GetCurrentLength …

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        delegate int D_GetBufferCount(IntPtr self, out uint count);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        delegate int D_GetBufferByIndex(IntPtr self, uint index, out IntPtr buffer);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        delegate int D_Lock(IntPtr self, out IntPtr buffer, out int maxLength, out int currentLength);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        delegate int D_Unlock(IntPtr self);

        static IntPtr VtblSlot(IntPtr p, int index)
        {
            IntPtr vtbl = Marshal.ReadIntPtr(p);
            return Marshal.ReadIntPtr(vtbl, index * IntPtr.Size);
        }

        // 同一个进程里 mfplat 只有一个模块，函数地址恒定，取一次缓存住
        // （每帧几次 GetDelegateForFunctionPointer 会明显吃 CPU）
        static Type tBufferCount = typeof(D_GetBufferCount);
        static Type tBufferByIndex = typeof(D_GetBufferByIndex);
        static Type tLock = typeof(D_Lock);
        static Type tUnlock = typeof(D_Unlock);
        static D_GetBufferCount fnBufferCount;
        static D_GetBufferByIndex fnBufferByIndex;
        static D_Lock fnLock;
        static D_Unlock fnUnlock;

        static void EnsureSampleFns(IntPtr sample)
        {
            if (fnBufferCount == null)
                fnBufferCount = (D_GetBufferCount)Marshal.GetDelegateForFunctionPointer(
                    VtblSlot(sample, 39), tBufferCount);
            if (fnBufferByIndex == null)
                fnBufferByIndex = (D_GetBufferByIndex)Marshal.GetDelegateForFunctionPointer(
                    VtblSlot(sample, 40), tBufferByIndex);
        }

        static void EnsureBufferFns(IntPtr buf)
        {
            if (fnLock == null) fnLock = (D_Lock)Marshal.GetDelegateForFunctionPointer(VtblSlot(buf, 3), tLock);
            if (fnUnlock == null) fnUnlock = (D_Unlock)Marshal.GetDelegateForFunctionPointer(VtblSlot(buf, 4), tUnlock);
        }

        [StructLayout(LayoutKind.Sequential)]
        struct PROPVARIANT
        {
            public ushort vt;             // VT_I8 = 20
            public ushort r1, r2, r3;
            public long value;
            public long pad;              // PROPVARIANT 在 x64 上是 24 字节
        }

        // ---------------- 解码器 ----------------

        /// <summary>一种「打开方式」的候选配置：URL 形式 × 是否带视频处理属性 × 想要的像素格式</summary>
        sealed class Combo
        {
            public string Name;
            public string Url;
            public bool Processing;
            public Guid Subtype;
            public Combo(string name, string url, bool processing, Guid subtype)
            {
                Name = name; Url = url; Processing = processing; Subtype = subtype;
            }
        }

        public sealed class Reader : IDisposable
        {
            // 11-16 晚：记住上一次成功的打开方式，下次先试它。
            //
            // 背景：PlayerVideo 播完一轮是「关掉解码器再打开」（SetCurrentPosition 实测会让
            // 进程静默消失，见 DecodeLoop 注释）。而 H.264 解码器原生只吐 NV12，
            // 每轮都从 RGB32 开始试就必然先失败一次 0xc00d36b4 ——
            // 日志每 4.5 秒刷一条「设定输出格式失败」，还白花几十毫秒在注定失败的协商上。
            // 现在有了这个记忆，第二轮起一次谈成；真遇到文件换了编码，仍然会回退到完整扫描。
            static int lastGoodCombo = -1;

            /// <summary>上一次 Open 试了几种配置（自检用来钉住「第二轮只试 1 次」）</summary>
            public static int LastAttempts;
            /// <summary>上一次 Open 采用的配置名，失败时为空串</summary>
            public static string LastGoodName = "";

            /// <summary>忘掉记住的配置（换片子/换设备后想重新全扫描时用）</summary>
            public static void ForgetCombo() { lastGoodCombo = -1; }

            IMFSourceReader reader;
            public int Width;
            public int Height;
            public int Stride;            // 源帧行字节数，可能为负（自下而上）
            public bool IsNv12;           // true = 解码器只给 NV12，需要我们自己转 RGB

            // 复用缓冲，避免每帧分配（NV12 转换用）
            byte[] yuvBuf;
            byte[] rgbBuf;
            bool warnedMultiBuffer;

            public static Reader Open(string path)
            {
                if (!mfStarted)
                {
                    int hr0 = MFStartup(MF_VERSION, 0);
                    if (hr0 < 0) throw new Exception("MFStartup 失败 0x" + hr0.ToString("x8"));
                    mfStarted = true;
                }

                // 11:48 实机日志说明的事：
                //   · 带 attributes（视频处理开关）去建 SourceReader → 0x80070057 E_INVALIDARG；
                //   · 不带 attributes 时文件本身是能打开的（更早那轮走到了 SetCurrentMediaType 才报 0xc00d36b4）。
                // 所以不再一次只赌一种配置：URL 形式 × 是否带属性 × 目标像素格式，全部试一遍，
                // 取第一个真能出帧的。RGB32 谈不下来就退到 NV12 自己转（H.264 解码器原生只出 NV12）。
                string fileUrl = "file:///" + path.Replace('\\', '/');
                Combo[] combos = new Combo[]
                {
                    new Combo("本地路径 · 不带属性 · RGB32", path, false, MFVideoFormat_RGB32),
                    new Combo("本地路径 · 不带属性 · NV12（本进程转 RGB）", path, false, MFVideoFormat_NV12),
                    new Combo("本地路径 · 带视频处理属性 · RGB32", path, true, MFVideoFormat_RGB32),
                    new Combo("file:/// · 不带属性 · RGB32", fileUrl, false, MFVideoFormat_RGB32),
                    new Combo("file:/// · 不带属性 · NV12（本进程转 RGB）", fileUrl, false, MFVideoFormat_NV12),
                    new Combo("file:/// · 带视频处理属性 · RGB32", fileUrl, true, MFVideoFormat_RGB32)
                };

                for (int k = 0; k < combos.Length; k++)
                {
                    // 先试上次成功那档，再按原顺序扫其余配置（上次那档本身不重复试）
                    int i = k;
                    if (lastGoodCombo >= 0 && k == 0) i = lastGoodCombo;
                    else if (lastGoodCombo >= 0 && k <= lastGoodCombo) i = k - 1;
                    Combo c = combos[i];
                    LastAttempts = k + 1;

                    IMFAttributes attrs = null;
                    if (c.Processing)
                    {
                        int hrA = MFCreateAttributes(out attrs, 2);
                        if (hrA < 0) attrs = null;
                        else
                        {
                            Guid kProc = MF_SOURCE_READER_ENABLE_VIDEO_PROCESSING;
                            Guid kAdv = MF_SOURCE_READER_ENABLE_ADVANCED_VIDEO_PROCESSING;
                            attrs.SetUINT32(ref kProc, 1);
                            attrs.SetUINT32(ref kAdv, 1);
                        }
                    }

                    IMFSourceReader sr = null;
                    int hr;
                    try
                    {
                        // attrs 只用于建 reader，建完即可释放；下面每条 continue/return 都
                        // 曾漏掉它，反复换片会累积 COM 引用。同函数里 nat/cur/mt/sample/buf
                        // 都有配对释放，唯独 attrs 缺，这里补齐。
                        hr = MFCreateSourceReaderFromURL(c.Url, attrs, out sr);
                    }
                    finally
                    {
                        if (attrs != null) Marshal.ReleaseComObject(attrs);
                        attrs = null;
                    }
                    if (hr < 0 || sr == null)
                    {
                        if (!Quiet) Log.Note("[MF] 轮 " + (i + 1) + " " + c.Name + " → 建 reader 失败 0x" + hr.ToString("x8"));
                        continue;
                    }

                    // 只留视频流：先全关，再单开第一路视频
                    sr.SetStreamSelection(MF_SOURCE_READER_ALL_STREAMS, 0);
                    sr.SetStreamSelection(MF_SOURCE_READER_FIRST_VIDEO_STREAM, 1);

                    // 诊断：把原生类型打出来，失败时才知道是哪种编码/像素格式卡住
                    IMFMediaType nat;
                    if (sr.GetNativeMediaType(MF_SOURCE_READER_FIRST_VIDEO_STREAM, 0, out nat) >= 0 && nat != null)
                    {
                        Guid kSub = MF_MT_SUBTYPE;
                        Guid sub;
                        nat.GetGUID(ref kSub, out sub);
                        Guid kSz = MF_MT_FRAME_SIZE;
                        ulong sz;
                        nat.GetUINT64(ref kSz, out sz);
                        if (!Quiet) Log.Note("[MF]   原生 subtype=" + sub.ToString()
                                 + " 尺寸 " + (int)(sz >> 32) + "x" + (int)(sz & 0xFFFFFFFFUL));
                        Marshal.ReleaseComObject(nat);
                    }

                    hr = Configure(sr, c.Subtype);
                    if (hr < 0)
                    {
                        if (!Quiet) Log.Note("[MF]   设定输出格式失败 0x" + hr.ToString("x8"));
                        Marshal.ReleaseComObject(sr);
                        continue;
                    }

                    IMFMediaType cur;
                    hr = sr.GetCurrentMediaType(MF_SOURCE_READER_FIRST_VIDEO_STREAM, out cur);
                    if (hr < 0 || cur == null)
                    {
                        if (!Quiet) Log.Note("[MF]   读回当前格式失败 0x" + hr.ToString("x8"));
                        Marshal.ReleaseComObject(sr);
                        continue;
                    }

                    Reader r = new Reader();
                    r.reader = sr;
                    r.IsNv12 = (c.Subtype == MFVideoFormat_NV12);

                    Guid kSize = MF_MT_FRAME_SIZE;
                    ulong size;
                    cur.GetUINT64(ref kSize, out size);
                    r.Width = (int)(size >> 32);
                    r.Height = (int)(size & 0xFFFFFFFFUL);

                    Guid kStride = MF_MT_DEFAULT_STRIDE;
                    uint stride;
                    if (cur.GetUINT32(ref kStride, out stride) < 0 || stride == 0 || stride == 0xFFFFFFFF)
                        stride = (uint)(r.IsNv12 ? r.Width : r.Width * 4);
                    r.Stride = (int)stride;
                    Marshal.ReleaseComObject(cur);

                    if (r.Width <= 0 || r.Height <= 0)
                    {
                        if (!Quiet) Log.Note("[MF]   尺寸读不出来，换下一个配置");
                        Marshal.ReleaseComObject(sr);
                        continue;
                    }

                    if (!Quiet) Log.Note("[MF] ✔ 采用「" + c.Name + "」 " + r.Width + "x" + r.Height
                             + " stride=" + r.Stride
                             + (r.IsNv12 ? "（解码器只给 NV12，本进程转 RGB）" : "（RGB32 直通）"));
                    lastGoodCombo = i;          // 下次先试这一档
                    LastGoodName = c.Name;
                    return r;
                }

                throw new Exception("6 种打开方式全部失败：视频文件可能损坏，或系统缺少 MF 解码器。");
            }

            /// <summary>把 reader 的输出格式设成指定像素格式</summary>
            static int Configure(IMFSourceReader sr, Guid subtype)
            {
                IMFMediaType mt;
                int hr = MFCreateMediaType(out mt);
                if (hr < 0) return hr;
                try
                {
                    Guid k1 = MF_MT_MAJOR_TYPE;
                    Guid v1 = MFMediaType_Video;
                    mt.SetGUID(ref k1, ref v1);
                    Guid k2 = MF_MT_SUBTYPE;
                    Guid v2 = subtype;
                    mt.SetGUID(ref k2, ref v2);
                    return sr.SetCurrentMediaType(MF_SOURCE_READER_FIRST_VIDEO_STREAM, IntPtr.Zero, mt);
                }
                finally
                {
                    Marshal.ReleaseComObject(mt);
                }
            }

            /// <summary>取一帧并拷进目标 DIB。返回 false 表示这轮播放到头了</summary>
            public bool NextFrame(IntPtr dst, int dstStride, out long timestamp)
            {
                timestamp = 0;
                for (int guard = 0; guard < 32; guard++)
                {
                    uint actual, flags;
                    long ts;
                    IntPtr sample;
                    int hr = reader.ReadSample(MF_SOURCE_READER_FIRST_VIDEO_STREAM, 0, out actual, out flags, out ts, out sample);
                    if (hr < 0) throw new Exception("ReadSample 失败 0x" + hr.ToString("x8"));

                    // 真到头了
                    if ((flags & MF_SOURCE_READERF_ENDOFSTREAM) != 0)
                    {
                        if (sample != IntPtr.Zero) Marshal.Release(sample);
                        return false;
                    }
                    // 空洞（只更新时间戳、没给帧）：继续读，别当成播放结束
                    if (sample == IntPtr.Zero) continue;

                    timestamp = ts;
                    try { CopyToDib(sample, dst, dstStride); }
                    finally { Marshal.Release(sample); }
                    return true;
                }
                return false;
            }

            void CopyToDib(IntPtr sample, IntPtr dst, int dstStride)
            {
                EnsureSampleFns(sample);

                uint count;
                int hr = fnBufferCount(sample, out count);
                if (hr < 0) throw new Exception("GetBufferCount 失败 0x" + hr.ToString("x8"));
                if (count == 0) throw new Exception("样本里一个缓冲都没有");
                if (count > 1 && !warnedMultiBuffer)
                {
                    warnedMultiBuffer = true;
                    if (!Quiet) Log.Note("[MF] 注意：这一帧有 " + count + " 个缓冲，只取第一个");
                }

                IntPtr buf;
                hr = fnBufferByIndex(sample, 0, out buf);
                if (hr < 0) throw new Exception("GetBufferByIndex 失败 0x" + hr.ToString("x8"));
                if (buf == IntPtr.Zero) throw new Exception("GetBufferByIndex 给了空缓冲");

                try
                {
                    EnsureBufferFns(buf);
                    IntPtr src;
                    int maxLen, curLen;
                    hr = fnLock(buf, out src, out maxLen, out curLen);
                    if (hr < 0) throw new Exception("Lock 失败 0x" + hr.ToString("x8"));
                    try
                    {
                        if (IsNv12) ConvertNv12(src, curLen, dst, dstStride);
                        else CopyRgb32(src, dst, dstStride);
                    }
                    finally
                    {
                        fnUnlock(buf);
                    }
                }
                finally
                {
                    Marshal.Release(buf);
                }
            }

            void CopyRgb32(IntPtr src, IntPtr dst, int dstStride)
            {
                int rowBytes = Math.Min(Width * 4, dstStride);
                int srcStride = Math.Abs(Stride);
                for (int y = 0; y < Height; y++)
                {
                    int sy = Stride >= 0 ? y : Height - 1 - y;
                    CopyMemory(new IntPtr(dst.ToInt64() + (long)y * dstStride),
                               new IntPtr(src.ToInt64() + (long)sy * srcStride),
                               new UIntPtr((uint)rowBytes));
                }
            }

            /// <summary>
            /// NV12 → BGRA（H.264 解码器的原生输出）。之前一直指望 MF 的视频处理器帮我们转 RGB32，
            /// 但那要求创建 SourceReader 时就带上处理属性，而本机带属性建 reader 直接 E_INVALIDARG。
            /// 自己转反而更稳：一次 Marshal.Copy 拿到整块 YUV，然后按 BT.709 逐像素算。
            /// </summary>
            void ConvertNv12(IntPtr src, int len, IntPtr dst, int dstStride)
            {
                int w = Width, h = Height;
                int sStride = Math.Abs(Stride);
                if (sStride < w) sStride = w;

                // 关键：解码器给的 NV12 缓冲，高度通常按 16 对齐（1080 → 1088）。
                // UV 平面真正从 stride * 对齐高度 开始，而 MF 报的帧高度只有 1080。
                // 按 1080 算偏移，顶部 16 行会把 Y 平面后面的填充字节当成 U/V ——
                // 填充全是 0，U=V=0 转出来正好是纯绿 (0,230,0)，这就是屏幕上那条绿边。
                // 用缓冲总长度反推真实的对齐高度：len = stride * alignedH * 3 / 2。
                int alignedH = h;
                if (len > 0 && sStride > 0)
                {
                    int est = (int)((long)len * 2L / (3L * sStride));
                    if (est >= h && est <= h + 128) alignedH = est;
                    else
                    {
                        int a = ((h + 15) / 16) * 16;           // 退一步：按 16 对齐猜
                        if ((long)a * sStride * 3 / 2 <= len) alignedH = a;
                    }
                }

                int uvOff = sStride * alignedH;
                int need = uvOff + sStride * (alignedH / 2);
                if (need > len) need = len;                 // 缓冲比理论值小就按实际来

                if (yuvBuf == null || yuvBuf.Length < need) yuvBuf = new byte[need];
                Marshal.Copy(src, yuvBuf, 0, need);

                int rowBytes = w * 4;
                int frameBytes = rowBytes * h;
                if (rgbBuf == null || rgbBuf.Length < frameBytes) rgbBuf = new byte[frameBytes];

                for (int y = 0; y < h; y++)
                {
                    int dstRow = y * rowBytes;
                    int yRow = y * sStride;
                    if (yRow + w > len)                         // 数据不够，剩下的行留黑
                    {
                        Array.Clear(rgbBuf, dstRow, rowBytes);
                        continue;
                    }

                    int uvRow = uvOff + (y >> 1) * sStride;
                    int o = dstRow;
                    for (int x = 0; x < w; x += 2)
                    {
                        int Y0 = yuvBuf[yRow + x];
                        int Y1 = (x + 1 < w) ? yuvBuf[yRow + x + 1] : Y0;

                        int U = 128, V = 128;
                        int iu = uvRow + x;
                        if (iu + 1 < need) { U = yuvBuf[iu]; V = yuvBuf[iu + 1]; }

                        int u = U - 128, v = V - 128;
                        // BT.709 有限范围，系数放大 1024 倍做整数运算
                        int rv = (1836 * v) >> 10;
                        int gu = (218 * u) >> 10;
                        int gv = (546 * v) >> 10;
                        int bu = (2163 * u) >> 10;

                        int yy0 = (1192 * (Y0 - 16)) >> 10;
                        int r0 = yy0 + rv, g0 = yy0 - gu - gv, b0 = yy0 + bu;
                        rgbBuf[o] = Clamp(b0); rgbBuf[o + 1] = Clamp(g0); rgbBuf[o + 2] = Clamp(r0); rgbBuf[o + 3] = 255;
                        o += 4;

                        if (x + 1 < w)
                        {
                            int yy1 = (1192 * (Y1 - 16)) >> 10;
                            int r1 = yy1 + rv, g1 = yy1 - gu - gv, b1 = yy1 + bu;
                            rgbBuf[o] = Clamp(b1); rgbBuf[o + 1] = Clamp(g1); rgbBuf[o + 2] = Clamp(r1); rgbBuf[o + 3] = 255;
                            o += 4;
                        }
                    }
                }

                // 一次性拷整帧。之前是每行一次 Marshal.Copy（1080 次/帧），
                // 光 P/Invoke 固定开销就吃掉几毫秒，30fps 直接被拖成 20 出头。
                if (dstStride == rowBytes)
                {
                    Marshal.Copy(rgbBuf, 0, dst, frameBytes);
                }
                else
                {
                    int copyBytes = Math.Min(rowBytes, dstStride);
                    for (int y = 0; y < h; y++)
                        Marshal.Copy(rgbBuf, y * rowBytes, new IntPtr(dst.ToInt64() + (long)y * dstStride), copyBytes);
                }
            }

            static byte Clamp(int v)
            {
                if (v < 0) return 0;
                if (v > 255) return 255;
                return (byte)v;
            }

            /// <summary>回到开头（循环播放）。失败返回 false，调用方可以改为重开文件</summary>
            public bool Rewind()
            {
                PROPVARIANT pv = new PROPVARIANT();
                pv.vt = 20;          // VT_I8
                pv.value = 0;
                int hr = reader.SetCurrentPosition(IntPtr.Zero, ref pv);
                if (hr < 0)
                {
                    Log.Note("SetCurrentPosition 失败 0x" + hr.ToString("x8"));
                    return false;
                }
                return true;
            }

            public void Dispose()
            {
                if (reader != null)
                {
                    try { Marshal.ReleaseComObject(reader); }
                    catch { }
                    reader = null;
                }
                if (mfStarted)
                {
                    try { MFShutdown(); }
                    catch { }
                    mfStarted = false;
                }
            }
        }
    }
}
