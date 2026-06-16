package io.glyph11

import java.lang.foreign.Arena
import java.lang.foreign.FunctionDescriptor
import java.lang.foreign.Linker
import java.lang.foreign.MemorySegment
import java.lang.foreign.SymbolLookup
import java.lang.foreign.ValueLayout
import java.lang.invoke.MethodHandle

/** A byte range (offset + length) into the parsed input buffer (zero-copy). */
data class Glyph11Span(val offset: Int, val length: Int)

/** A parsed name/value pair (header or query parameter); spans index into the input. */
data class Glyph11Field(val name: Glyph11Span, val value: Glyph11Span)

/** Parsed request fields. Spans index into the input passed to [Glyph11.parse]. */
data class Glyph11Result(
    val status: Int,
    val method: Glyph11Span,
    val target: Glyph11Span,
    val path: Glyph11Span,
    val version: Glyph11Span,
    val headers: List<Glyph11Field>,
    val query: List<Glyph11Field>,
    val consumed: Long,
) {
    val isOk: Boolean get() = status == 0
    val isIncomplete: Boolean get() = status == 1
    val headerCount: Int get() = headers.size
    val queryCount: Int get() = query.size
}

/**
 * Kotlin/JVM binding for the Glyph11 hardened HTTP/1.1 request parser
 * (`libglyph11`), via the Foreign Function & Memory API (Panama).
 *
 * Point at the native library with `-Dglyph11.lib=/path/to/libglyph11.so`,
 * otherwise it is resolved from the default library search path.
 */
object Glyph11 {
    // glyph11_request field offsets (see glyph11.h; LP64 layout).
    private const val OFF_HEADERS = 32L
    private const val OFF_HEADER_CAP = 40L
    private const val OFF_HEADER_COUNT = 44L
    private const val OFF_QUERY = 48L
    private const val OFF_QUERY_CAP = 56L
    private const val OFF_QUERY_COUNT = 60L
    private const val SIZEOF_REQUEST = 64L
    private const val SIZEOF_FIELD = 16L
    private const val CAPACITY = 256

    private val linker = Linker.nativeLinker()

    private val lookup: SymbolLookup = run {
        val path = System.getProperty("glyph11.lib")
        if (path != null) SymbolLookup.libraryLookup(path, Arena.global())
        else SymbolLookup.libraryLookup("glyph11", Arena.global())
    }

    private fun handle(name: String, desc: FunctionDescriptor): MethodHandle =
        linker.downcallHandle(lookup.find(name).orElseThrow { UnsatisfiedLinkError(name) }, desc)

    private val parseHandle = handle(
        "glyph11_parse_request",
        FunctionDescriptor.of(
            ValueLayout.JAVA_INT,
            ValueLayout.ADDRESS,    // const uint8_t* buf
            ValueLayout.JAVA_LONG,  // size_t len
            ValueLayout.ADDRESS,    // const glyph11_limits* (null -> defaults)
            ValueLayout.ADDRESS,    // glyph11_request*
            ValueLayout.ADDRESS,    // size_t* consumed
        ),
    )

    private val httpCodeHandle = handle(
        "glyph11_status_http_code",
        FunctionDescriptor.of(ValueLayout.JAVA_INT, ValueLayout.JAVA_INT),
    )

    private val abiHandle = handle(
        "glyph11_abi_version",
        FunctionDescriptor.of(ValueLayout.JAVA_INT),
    )

    /** Packed ABI version of the loaded native library. */
    val abiVersion: Int get() = abiHandle.invoke() as Int

    /** HTTP response code for a status (400 / 431, or 0 for OK / incomplete). */
    fun httpCode(status: Int): Int = httpCodeHandle.invoke(status) as Int

    /** Parse one HTTP/1.1 request header block from [input]. */
    fun parse(input: ByteArray): Glyph11Result {
        Arena.ofConfined().use { arena ->
            val buf = arena.allocate(maxOf(input.size, 1).toLong())
            MemorySegment.copy(input, 0, buf, ValueLayout.JAVA_BYTE, 0L, input.size)

            val headers = arena.allocate(SIZEOF_FIELD * CAPACITY)
            val query = arena.allocate(SIZEOF_FIELD * CAPACITY)
            val req = arena.allocate(SIZEOF_REQUEST) // zero-initialized
            req.set(ValueLayout.ADDRESS, OFF_HEADERS, headers)
            req.set(ValueLayout.JAVA_INT, OFF_HEADER_CAP, CAPACITY)
            req.set(ValueLayout.ADDRESS, OFF_QUERY, query)
            req.set(ValueLayout.JAVA_INT, OFF_QUERY_CAP, CAPACITY)
            val consumed = arena.allocate(ValueLayout.JAVA_LONG)

            val status = parseHandle.invoke(
                buf, input.size.toLong(), MemorySegment.NULL, req, consumed,
            ) as Int

            fun span(off: Long) =
                Glyph11Span(req.get(ValueLayout.JAVA_INT, off), req.get(ValueLayout.JAVA_INT, off + 4))
            fun fields(seg: MemorySegment, count: Int): List<Glyph11Field> =
                (0 until count).map { i ->
                    val b = i.toLong() * SIZEOF_FIELD
                    Glyph11Field(
                        Glyph11Span(seg.get(ValueLayout.JAVA_INT, b), seg.get(ValueLayout.JAVA_INT, b + 4)),
                        Glyph11Span(seg.get(ValueLayout.JAVA_INT, b + 8), seg.get(ValueLayout.JAVA_INT, b + 12)),
                    )
                }

            return Glyph11Result(
                status = status,
                method = span(0L),
                target = span(8L),
                path = span(16L),
                version = span(24L),
                headers = if (status == 0) fields(headers, req.get(ValueLayout.JAVA_INT, OFF_HEADER_COUNT)) else emptyList(),
                query = if (status == 0) fields(query, req.get(ValueLayout.JAVA_INT, OFF_QUERY_COUNT)) else emptyList(),
                consumed = if (status == 0) consumed.get(ValueLayout.JAVA_LONG, 0L) else 0L,
            )
        }
    }

    /**
     * Benchmark helper: parse [input] [iters] times, returning ns/op. Contiguous
     * ([multiSeg] = false) reuses one native buffer; multi-segment allocates a fresh
     * native buffer (a per-call [Arena]) each request, mirroring real linearization.
     */
    fun benchParse(input: ByteArray, iters: Long, multiSeg: Boolean = false): Double {
        Arena.ofConfined().use { arena ->
            val buf = arena.allocate(maxOf(input.size, 1).toLong())
            if (!multiSeg) MemorySegment.copy(input, 0, buf, ValueLayout.JAVA_BYTE, 0L, input.size)
            val s1 = input.size / 3
            val s2 = 2 * input.size / 3
            val headers = arena.allocate(SIZEOF_FIELD * CAPACITY)
            val query = arena.allocate(SIZEOF_FIELD * CAPACITY)
            val req = arena.allocate(SIZEOF_REQUEST)
            val consumed = arena.allocate(ValueLayout.JAVA_LONG)
            val len = input.size.toLong()

            // Limits matching the other benches (max 200 headers, 64 KiB total) so the
            // 32 KB payload (153 headers) parses fully instead of being rejected early
            // with TOO_MANY_HEADERS under the default 100-header cap.
            val lim = arena.allocate(32)
            lim.set(ValueLayout.JAVA_INT, 0L, 32)          // struct_size
            lim.set(ValueLayout.JAVA_INT, 4L, 200)         // max_header_count
            lim.set(ValueLayout.JAVA_INT, 8L, 256)         // max_header_name_length
            lim.set(ValueLayout.JAVA_INT, 12L, 8192)       // max_header_value_length
            lim.set(ValueLayout.JAVA_INT, 16L, 8192)       // max_url_length
            lim.set(ValueLayout.JAVA_INT, 20L, 128)        // max_query_param_count
            lim.set(ValueLayout.JAVA_INT, 24L, 16)         // max_method_length
            lim.set(ValueLayout.JAVA_INT, 28L, 64 * 1024)  // max_total_header_bytes

            fun parseInto(b: MemorySegment) {
                req.set(ValueLayout.ADDRESS, OFF_HEADERS, headers)
                req.set(ValueLayout.JAVA_INT, OFF_HEADER_CAP, CAPACITY)
                req.set(ValueLayout.ADDRESS, OFF_QUERY, query)
                req.set(ValueLayout.JAVA_INT, OFF_QUERY_CAP, CAPACITY)
                parseHandle.invoke(b, len, lim, req, consumed)
            }
            fun once() {
                if (multiSeg) {
                    // a real binding linearizes a fresh native buffer per request (no reuse)
                    Arena.ofConfined().use { tmp ->
                        val b = tmp.allocate(len)
                        MemorySegment.copy(input, 0, b, ValueLayout.JAVA_BYTE, 0L, s1)
                        MemorySegment.copy(input, s1, b, ValueLayout.JAVA_BYTE, s1.toLong(), s2 - s1)
                        MemorySegment.copy(input, s2, b, ValueLayout.JAVA_BYTE, s2.toLong(), input.size - s2)
                        parseInto(b)
                    }
                } else {
                    parseInto(buf)
                }
            }

            var w = 0L
            while (w < iters / 10 + 1) { once(); w++ } // warmup (also lets the JIT compile)
            var best = Double.MAX_VALUE // best of N trials filters scheduling / turbo interference
            for (trial in 0 until 5) {
                val t0 = System.nanoTime()
                var i = 0L
                while (i < iters) { once(); i++ }
                val t1 = System.nanoTime()
                val ns = (t1 - t0).toDouble() / iters
                if (ns < best) best = ns
            }
            return best
        }
    }
}
