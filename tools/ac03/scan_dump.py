"""从 minidump 里捞"崩溃线程在干什么"——不依赖 windbg / dotnet-dump。

本地转储由 Sysinternals procdump（`-e -ma`）生成。这台机器上没有 cdb，也没有
dotnet-dump，而真正需要的只是三件事：异常代码与地址、故障地址落在哪个模块的
哪个偏移、崩溃线程栈上有哪些返回地址。

全内存转储（`-ma`）里栈数据不在 ThreadListStream 的 MemoryLocation 上，而在
Memory64ListStream 的顺序数据块里，因此按地址读内存（`read_mem`）是唯一可靠的
取栈方式。返回地址靠扫栈得到：不精确，但足够把调用方指到具体模块与偏移上。

用法：
    python tools/ac03/scan_dump.py D:\\ac03\\dumps\\Jianlan.exe_261115_095549.dmp
    python tools/ac03/scan_dump.py <dmp> --export     # 顺带用导出表标注最近的函数名
"""
from __future__ import annotations

import struct
import sys
from pathlib import Path

THREAD_LIST, MODULE_LIST, EXCEPTION, MEMORY64_LIST, MEMORY_LIST = 3, 4, 6, 9, 5

# AMD64 CONTEXT 里我们关心的几个偏移（CONTEXT 结构本身 1232 字节）。
# 注意 Dr4/Dr5 在 AMD64 里被省略，所以整数寄存器从 0x78 起：
# Rax .78  Rcx .80  Rdx .88  Rbx .90  Rsp .98  Rbp .a0  Rsi .a8  Rdi .b0
# R8 .b8   R9 .c0   R10 .c8  R11 .d0  R12 .d8  R13 .e0  R14 .e8  R15 .f0  Rip .f8
CTX_RSP, CTX_RBP, CTX_RIP = 0x98, 0xA0, 0xF8
CTX_ARGS = {0x80: "Rcx", 0x88: "Rdx", 0xB8: "R8", 0xC0: "R9"}


def u16(d, o):
    return struct.unpack_from("<H", d, o)[0]


def u32(d, o):
    return struct.unpack_from("<I", d, o)[0]


def u64(d, o):
    return struct.unpack_from("<Q", d, o)[0]


def read_string(d, rva):
    n = u32(d, rva)
    return d[rva + 4: rva + 4 + n].decode("utf-16-le", "replace")


def parse(d):
    assert d[:4] == b"MDMP", "不是 minidump"
    n, dir_rva = u32(d, 8), u32(d, 12)
    streams = {}
    for i in range(n):
        t, size, rva = struct.unpack_from("<III", d, dir_rva + i * 12)
        streams.setdefault(t, []).append((size, rva))

    mods = []
    if MODULE_LIST in streams:
        _, rva = streams[MODULE_LIST][0]
        cnt = u32(d, rva)
        for i in range(cnt):
            # MINIDUMP_MODULE 是 108 字节：BaseOfImage(8) SizeOfImage(4) ...
            # ModuleNameRva 在 +20
            o = rva + 4 + i * 108
            base, size = u64(d, o), u32(d, o + 8)
            name = read_string(d, u32(d, o + 20)).split("\\")[-1]
            mods.append((base, size, name))
    mods.sort()

    # 全内存转储用 Memory64List；普通转储用 MemoryList。两种都建索引，
    # 之后一律通过 read_mem 按虚拟地址取数据。
    ranges = []          # (vaddr_start, size, file_rva) 按 vaddr 升序
    if MEMORY64_LIST in streams:
        _, rva = streams[MEMORY64_LIST][0]
        cnt = u64(d, rva)
        base_rva = u64(d, rva + 8)
        cursor = base_rva
        for i in range(cnt):
            o = rva + 16 + i * 16
            start, size = u64(d, o), u64(d, o + 8)
            ranges.append((start, size, cursor))
            cursor += size
    elif MEMORY_LIST in streams:
        _, rva = streams[MEMORY_LIST][0]
        cnt = u32(d, rva)
        for i in range(cnt):
            o = rva + 4 + i * 16
            start, size, frva = u64(d, o), u32(d, o + 8), u32(d, o + 12)
            ranges.append((start, size, frva))
    ranges.sort()

    threads = []
    if THREAD_LIST in streams:
        _, rva = streams[THREAD_LIST][0]
        cnt = u32(d, rva)
        for i in range(cnt):
            # MINIDUMP_THREAD = 48 字节：ThreadId(4) Suspend(4) PriClass(4) Pri(4)
            # Teb(8) Stack{Start(8) Size(4) Rva(4)}(24..39) ThreadContext{Size(4) Rva(4)}(40..47)
            o = rva + 4 + i * 48
            threads.append({
                "tid": u32(d, o),
                "stack_start": u64(d, o + 24),
                "stack_size": u32(d, o + 32),
                "ctx_rva": u32(d, o + 44),
            })

    exc = None
    if EXCEPTION in streams:
        _, rva = streams[EXCEPTION][0]
        # MINIDUMP_EXCEPTION_STREAM: ThreadId(4) align(4)，MINIDUMP_EXCEPTION 从 +8 起：
        # Code(8) Flags(12) Record(16) Address(24) NumberParameters(32) align(36)
        # ExceptionInformation[15](40..159)，随后 ThreadContext{Size(4) Rva(4)}(160..167)
        code = u32(d, rva + 8)
        addr = u64(d, rva + 24)
        nparam = u32(d, rva + 32)
        params = [u64(d, rva + 40 + 8 * k) for k in range(min(nparam, 15))]
        exc = {"tid": u32(d, rva), "code": code, "addr": addr, "params": params,
               "ctx_rva": u32(d, rva + 164),
               "ctx_size": u32(d, rva + 160)}
    return mods, threads, exc, ranges


def make_reader(d, ranges):
    def read_mem(addr: int, size: int) -> bytes | None:
        """按虚拟地址读内存；跨区间时不拼接（栈一般落在单块里）。"""
        lo, hi = 0, len(ranges) - 1
        while lo <= hi:
            mid = (lo + hi) // 2
            start, rsize, frva = ranges[mid]
            if start <= addr < start + rsize:
                off = addr - start
                if off + size > rsize:
                    size = rsize - off
                return d[frva + off: frva + off + size]
            if addr < start:
                hi = mid - 1
            else:
                lo = mid + 1
        return None
    return read_mem


def owner(mods, addr):
    lo, hi = 0, len(mods) - 1
    while lo <= hi:
        mid = (lo + hi) // 2
        base, size, name = mods[mid]
        if base <= addr < base + size:
            return name, addr - base
        if addr < base:
            hi = mid - 1
        else:
            lo = mid + 1
    return None


# ---- 导出表：把模块内偏移还原成最近的导出函数名（只对需要的模块做，缓存结果）----
_export_cache: dict[str, list[tuple[int, str]]] = {}


def exports_of(module_name: str):
    """从磁盘上的同名 DLL 读导出表（dump 里没有导出信息）。"""
    if module_name in _export_cache:
        return _export_cache[module_name]
    out: list[tuple[int, str]] = []
    for base_dir in (r"C:\Windows\System32", r"C:\Windows\SysWOW64", r"C:\Windows"):
        p = Path(base_dir) / module_name
        if not p.exists():
            continue
        try:
            d = p.read_bytes()
            pe = u32(d, 0x3C)
            if d[pe:pe + 4] != b"PE\0\0":
                continue
            nsec = u16(d, pe + 6)
            opt = pe + 24
            magic = u16(d, opt)
            dd = opt + (0x70 if magic == 0x20B else 0x60)
            exp_rva = u32(d, dd)
            sec_off = opt + u16(d, pe + 20)
            secs = []
            for i in range(nsec):
                o = sec_off + i * 40
                vsize, vaddr, rsize, raddr = struct.unpack_from("<IIII", d, o + 8)
                secs.append((vaddr, max(vsize, rsize), raddr))

            def r2o(rva):
                for vaddr, size, raddr in secs:
                    if vaddr <= rva < vaddr + size:
                        return raddr + (rva - vaddr)
                return None

            o = r2o(exp_rva)
            if o is None:
                continue
            nfun, nname = u32(d, o + 20), u32(d, o + 24)
            afun, aname, aord = u32(d, o + 28), u32(d, o + 32), u32(d, o + 36)
            funcs = [u32(d, r2o(afun) + 4 * i) for i in range(nfun)]
            for i in range(nname):
                nrva = u32(d, r2o(aname) + 4 * i)
                no = r2o(nrva)
                end = d.index(b"\0", no)
                name = d[no:end].decode("ascii", "replace")
                ordi = u16(d, r2o(aord) + 2 * i)
                if ordi < len(funcs):
                    out.append((funcs[ordi], name))
            out.sort()
        except Exception:
            pass
        if out:
            break
    _export_cache[module_name] = out
    return out


def nearest_export(module_name: str, off: int) -> str:
    ex = exports_of(module_name)
    if not ex:
        return ""
    lo, hi, best = 0, len(ex) - 1, None
    while lo <= hi:
        mid = (lo + hi) // 2
        if ex[mid][0] <= off:
            best = ex[mid]
            lo = mid + 1
        else:
            hi = mid - 1
    if not best:
        return ""
    return f"  ← {best[1]}+0x{off - best[0]:x}"


def main() -> int:
    args = [a for a in sys.argv[1:] if not a.startswith("--")]
    want_export = "--export" in sys.argv
    path = Path(args[0] if args else "")
    if not path.exists():
        print(f"找不到转储：{path}")
        return 2
    d = path.read_bytes()
    mods, threads, exc, ranges = parse(d)
    read_mem = make_reader(d, ranges)
    print(f"转储 {path.name}  {len(d)/1048576:.1f} MB；模块 {len(mods)}；"
          f"线程 {len(threads)}；内存块 {len(ranges)}")

    if not exc:
        print("没有异常流，转储不是崩溃现场")
        return 1

    hit = owner(mods, exc["addr"])
    print(f"\n异常 0x{exc['code']:08X}  地址 0x{exc['addr']:X}  "
          f"({hit[0]}+0x{hit[1]:x})" if hit else f"\n异常 0x{exc['code']:08X}  地址 0x{exc['addr']:X}")
    if exc["code"] == 0xC0000005 and len(exc["params"]) >= 2:
        kind = {0: "读取", 1: "写入", 8: "执行"}.get(exc["params"][0], "?")
        print(f"访问违例：{kind} 0x{exc['params'][1]:X}")

    ctx_rva = exc["ctx_rva"]
    rsp, rbp, rip = (u64(d, ctx_rva + CTX_RSP), u64(d, ctx_rva + CTX_RBP),
                     u64(d, ctx_rva + CTX_RIP)) if ctx_rva else (0, 0, 0)
    print(f"崩溃线程 {exc['tid']}  Rip={rip:#x}  Rsp={rsp:#x}  Rbp={rbp:#x}")
    if ctx_rva:
        args = {name: u64(d, ctx_rva + off) for off, name in CTX_ARGS.items()}
        print("参数寄存器 " + "  ".join(f"{k}={v:#x}" for k, v in args.items()))

    if not rsp:
        print("没有可用的上下文（转储未带 CONTEXT），无法回溯")
        return 1

    # 先走 RBP 链——这是最接近真实调用栈的东西。x64 代码常在调试版里保留帧指针，
    # 系统 DLL 多半保留；链断了再退回扫栈。
    print("\nRBP 链（[rbp] 上层帧、[rbp+8] 返回地址）：")
    cur, walked = rbp, 0
    for depth in range(28):
        frame = read_mem(cur, 16)
        if not frame:
            break
        nxt, ret = u64(frame, 0), u64(frame, 8)
        who = owner(mods, ret)
        if not who:
            break
        name, delta = who
        extra = nearest_export(name, delta) if want_export else ""
        print(f"  #{depth:<2} rbp={cur:#x}  ret={ret:#x}  {name}+0x{delta:x}{extra}")
        walked += 1
        if nxt <= cur:
            break
        cur = nxt
    if walked == 0:
        print("  （帧指针未保留，改用扫栈）")

    stack = read_mem(rsp, 0x40000)
    if not stack:
        print("栈内存不在转储里")
        return 1

    print(f"\n栈上候选返回地址（Rsp 起 {len(stack)} 字节，按 8 字节对齐，模块+偏移）：")
    shown = 0
    for off in range(0, len(stack) - 8, 8):
        v = struct.unpack_from("<Q", stack, off)[0]
        if v < 0x10000:
            continue
        who = owner(mods, v)
        if not who:
            continue
        name, delta = who
        extra = nearest_export(name, delta) if want_export else ""
        print(f"  rsp+0x{off:05x}  {v:#018x}  {name}+0x{delta:x}{extra}")
        shown += 1
        if shown >= 40:
            break
    return 0


if __name__ == "__main__":
    sys.exit(main())
