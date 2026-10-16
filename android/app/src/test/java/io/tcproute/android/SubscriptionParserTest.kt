package io.tcproute.android

import org.json.JSONObject
import org.json.JSONArray
import org.junit.Assert.*
import org.junit.Test
import java.util.Base64

class SubscriptionParserTest {
    @Test fun listsAndIdentities() {
        val plain = "socks5://u:p@127.0.0.1:1080#Node%20One\nhttps://proxy.test:443#Two"
        val nodes = SubscriptionParser.parse(plain)
        assertEquals("Node One", nodes[0].name)
        assertEquals(nodes.map { it.id }, SubscriptionParser.parse(Base64.getEncoder().encodeToString(plain.toByteArray())).map { it.id })
        assertEquals(nodes[0].id, SubscriptionParser.parse("socks5://new:secret@other.test:1081#Node%20One").single().id)
        assertEquals("a+b", SubscriptionParser.parse("socks5://proxy.test:1080#a+b").single().name)
        assertTrue(SubscriptionParser.parse("[]").isEmpty())
    }

    @Test fun clashFullProfileAndFlowMappings() {
        val nodes = SubscriptionParser.parse("""
            mixed-port: 7890
            proxies:
              - {name: 'With, comma', type: http, server: '2001:db8::1', port: 8080, username: 'a@b', password: 'c:d'}
              - name: SS
                type: ss
                server: ss.test
                port: 443
                cipher: aes-128-gcm
                password: 'secret@key'
            proxy-groups:
              - {name: Auto, type: select, proxies: [SS]}
            rules: ['MATCH,Auto']
        """.trimIndent())
        assertEquals(2, nodes.size)
        assertEquals("http://a%40b:c%3Ad@[2001:db8::1]:8080", nodes[0].proxyUrl)
        assertEquals("ss://aes-128-gcm:secret%40key@ss.test:443", nodes[1].proxyUrl)
    }

    @Test fun surgeFullProfileAndNamedOptions() {
        val nodes = SubscriptionParser.parse("""
            [General]
            loglevel = notify
            [Proxy]
            Local = direct
            HTTP = https, host.test, 443, "user,name", "pa,ss"
            SS = ss, ss.test, 8080, encrypt-method=aes-128-gcm, password="secret,word"
            Trojan = trojan, trojan.test, 443, password=pwd, sni=tls.test
            [Proxy Group]
            Auto = select, HTTP, SS
            [Rule]
            FINAL,Auto
        """.trimIndent())
        assertEquals(3, nodes.size)
        assertEquals("https://user%2Cname:pa%2Css@host.test:443", nodes[0].proxyUrl)
        assertTrue(nodes[1].proxyUrl.contains("secret%2Cword"))
        assertTrue(nodes[2].proxyUrl.contains("sni=tls.test"))
    }

    @Test fun vmessWebsocketPreservesParameters() {
        val node = SubscriptionParser.parse("""
            proxies:
              - name: VMess
                type: vmess
                server: vmess.test
                port: 443
                uuid: 11111111-1111-1111-1111-111111111111
                network: ws
                tls: true
                servername: tls.test
                ws-opts: {path: /socket, headers: {Host: ws.test}}
        """.trimIndent()).single()
        val json = JSONObject(String(Base64.getDecoder().decode(node.proxyUrl.substringAfter("://"))))
        assertEquals("/socket", json.getString("path")); assertEquals("ws.test", json.getString("host")); assertEquals("tls.test", json.getString("sni"))
    }

    @Test fun invalidUpdatesCannotMasqueradeAsEmpty() {
        for (bad in listOf("", "proxies: []", "[General]\nloglevel=notify", "socks5://a.test:1#Same\nsocks5://b.test:2#Same",
            "proxies: [{name: Bad, type: ss, server: host, port: 443}]", "proxies: [{name: Bad, type: snell, server: host, port: 443}]",
            "proxies: [{name: Bad, type: http, server: host, port: 443, skip-cert-verify: true}]",
            "proxies: !!java.net.URL [https://example.com]", "[Proxy]\nBad = http, host, 80, \"unterminated")) {
            try { SubscriptionParser.parse(bad); fail("Accepted invalid subscription") } catch (_: Exception) { }
        }
    }

    @Test fun defaultsAndLocksValidate() {
        AndroidSubscription(name = "my sub", url = "https://example.com/sub", lockedFields = setOf("Sleep")).validate()
        assertEquals("sub_my_sub_", AndroidSubscription(name = "my sub").prefix)
        for (options in listOf("{\"Sleep\":-1}", "{\"DnsResolve\":\"true\"}", "{\"Unknown\":1}", "{\"UpProxy\":\"a\",\"DialProxyFor\":\"b\"}")) {
            try { SubscriptionParser.validateOptions(JSONObject(options)); fail("Invalid defaults accepted") } catch (_: Exception) { }
        }
        val source = AndroidSubscription(name = "locked", url = "https://example.com/sub",
            defaults = JSONObject("{\"Credit\":-100,\"Sleep\":80,\"UpProxy\":\"front\"}"), lockedFields = setOf("Credit", "UpProxy"))
        val generated = SubscriptionStore.upstream(source, AndroidSubscriptionNode("a", "A", "https://proxy.test:443?credit=999&upProxy=wrong&extra=kept"))
        assertEquals(-100, generated.getInt("Credit")); assertEquals(80, generated.getInt("Sleep"))
        assertTrue(generated.getString("ProxyUrl").contains("upProxy=front")); assertFalse(generated.getString("ProxyUrl").contains("credit=")); assertTrue(generated.getString("ProxyUrl").contains("extra=kept"))
    }

    @Test fun coreBridgePreservesLifecycleAndRules() {
        val executable = System.getenv("TCPROUTE_TEST_CORE")
        org.junit.Assume.assumeTrue(executable != null)
        fun edit(content: String, imports: JSONArray = JSONArray(), removals: JSONArray = JSONArray(), includeRemoved: Boolean = false) =
            SubscriptionTomlEditor.editExecutable(executable!!, JSONObject().put("Content", content).put("Imports", imports).put("Removals", removals).put("IncludeRemoved", includeRemoved))
        val source = AndroidSubscription(id = "feed", name = "my sub", url = "https://example.com/sub")
        val node = AndroidSubscriptionNode("stable", "A", "socks5://proxy.test:1080")
        fun imports() = JSONArray().put(JSONObject().put("SourceId", source.id).put("NodeId", node.id).put("NodeName", node.name).put("Upstream", SubscriptionStore.upstream(source, node)))
        val original = "addr=\"127.0.0.1:7070\"\n[[Dns]]\nType=\"system\"\nPriority=20\n"
        val first = edit(original, imports()).getString("Content")
        val customized = first.replace("Sleep=80", "Sleep=19 # user") + "\n[[UpStreams.Whitelist]]\nPath=\"rules.txt\"\nType=\"suffix\"\n"
        val refreshed = edit(customized, imports()).getString("Content")
        assertTrue(refreshed.contains("Sleep=19 # user")); assertTrue(refreshed.contains("rules.txt")); assertTrue(refreshed.startsWith(original))
        val removed = edit(refreshed, removals = JSONArray().put(JSONObject().put("SourceId", source.id).put("NodeId", node.id)))
        assertEquals(0, removed.getJSONArray("Selections").length()); assertTrue(removed.getString("Content").contains("Remove=true"))
        assertEquals("stable", edit(removed.getString("Content"), includeRemoved = true).getJSONArray("Selections").getJSONObject(0).getString("NodeId"))
        val restored = edit(removed.getString("Content"), imports()).getString("Content")
        assertTrue(restored.contains("Remove=false")); assertTrue(restored.contains("Sleep=19 # user")); assertTrue(restored.contains("rules.txt"))
    }

    @Test fun downloadSendsSourceUserAgentAndRejectsBadResponses() {
        for (status in listOf(200, 403)) {
            java.net.ServerSocket(0, 1, java.net.InetAddress.getLoopbackAddress()).use { server ->
                var receivedAgent = ""
                val thread = Thread {
                    server.accept().use { socket ->
                        socket.soTimeout = 5000
                        val reader = socket.getInputStream().bufferedReader()
                        while (true) { val line = reader.readLine() ?: break; if (line.isEmpty()) break; if (line.startsWith("User-Agent:", true)) receivedAgent = line.substringAfter(':').trim() }
                        val body = "socks5://proxy.test:1080#Downloaded"
                        socket.getOutputStream().write(("HTTP/1.1 $status OK\r\nContent-Length: ${body.toByteArray().size}\r\nConnection: close\r\n\r\n" + body).toByteArray())
                    }
                }.apply { isDaemon = true; start() }
                val source = AndroidSubscription(name = "download", url = "http://127.0.0.1:${server.localPort}/feed", userAgent = "TcpRoute2-Test/1.1")
                if (status == 200) assertEquals("Downloaded", SubscriptionStore.download(source).single().name)
                else try { SubscriptionStore.download(source); fail("HTTP error accepted") } catch (_: IllegalArgumentException) { }
                thread.join(5000); assertFalse(thread.isAlive); assertEquals(source.userAgent, receivedAgent)
            }
        }
    }
}
