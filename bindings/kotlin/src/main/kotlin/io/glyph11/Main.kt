package io.glyph11

import java.io.File
import kotlin.system.exitProcess

fun main(args: Array<String>) {
    if (args.isNotEmpty() && args[0] == "bench") {
        bench(if (args.size > 1) args[1] else ".")
        return
    }
    smoke()
}

/** Emit `kotlin-ffi,<payload>,<ns>` for the cross-language aggregator. */
private fun bench(dir: String) {
    val cases = listOf(
        Triple("small", "small.bin", 2_000_000L),
        Triple("4k", "h4k.bin", 500_000L),
        Triple("32k", "h32k.bin", 100_000L),
    )
    for ((name, file, iters) in cases) {
        val data = File(dir, file).readBytes()
        println("kotlin-ffi,%s,%.1f".format(name, Glyph11.benchParse(data, iters)))
        println("kotlin-ffi-multiseg,%s,%.1f".format(name, Glyph11.benchParse(data, iters, multiSeg = true)))
    }

    val chunked = listOf(
        Triple("small", "chunked_small.bin", 1_000_000L),
        Triple("4k", "chunked_4k.bin", 300_000L),
        Triple("32k", "chunked_32k.bin", 50_000L),
    )
    for ((name, file, iters) in chunked) {
        val data = File(dir, file).readBytes()
        println("kotlin-ffi-chunked,%s,%.1f".format(name, Glyph11.benchChunked(data, iters)))
    }
}

/** Smoke test: parse a few requests via the native core and verify the results. */
private fun smoke() {
    println("glyph11 abi 0x%06x".format(Glyph11.abiVersion))

    var fails = 0
    fun check(name: String, cond: Boolean) {
        if (!cond) { fails++; println("  FAIL $name") }
    }
    fun bytes(s: String) = s.toByteArray(Charsets.ISO_8859_1)
    fun slice(b: ByteArray, sp: Glyph11Span) = String(b, sp.offset, sp.length, Charsets.ISO_8859_1)

    val valid = bytes("GET /api/users?a=1&b=2 HTTP/1.1\r\nHost: example.com\r\nAccept: */*\r\n\r\n")
    val r = Glyph11.parse(valid)
    check("valid ok", r.isOk)
    check("method", slice(valid, r.method) == "GET")
    check("path", slice(valid, r.path) == "/api/users")
    check("version", slice(valid, r.version) == "HTTP/1.1")
    check("headerCount", r.headerCount == 2)
    check("header name/value", r.headers[0].let { slice(valid, it.name) == "Host" && slice(valid, it.value) == "example.com" })
    check("queryCount", r.queryCount == 2)
    check("consumed", r.consumed.toInt() == valid.size)

    check("no-host -> 400", Glyph11.httpCode(Glyph11.parse(bytes("GET / HTTP/1.1\r\n\r\n")).status) == 400)
    check("te+cl -> 400", Glyph11.httpCode(Glyph11.parse(
        bytes("POST / HTTP/1.1\r\nHost: x\r\nTransfer-Encoding: chunked\r\nContent-Length: 5\r\n\r\n")).status) == 400)
    check("traversal -> 400", Glyph11.httpCode(Glyph11.parse(bytes("GET /a/../b HTTP/1.1\r\nHost: x\r\n\r\n")).status) == 400)
    check("incomplete", Glyph11.parse(bytes("GET / HTTP/1.1\r\nHost: x\r\n")).isIncomplete)

    if (fails == 0) {
        println("kotlin binding: all checks passed")
    } else {
        println("kotlin binding: $fails check(s) FAILED")
        exitProcess(1)
    }
}
