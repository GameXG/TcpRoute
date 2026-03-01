# TcpRoute2 协议配置手册（个人版）

## 1. 配置文件结构

`TcpRoute2` 默认读取当前目录 `config.toml`。

```toml
addr = "127.0.0.1:7070"

[[UpStreams]]
Name = "direct"
ProxyUrl = "direct://0.0.0.0:0000"
DnsResolve = true
Credit = 0
Sleep = 0
CorrectDelay = 0
```

字段说明：

- `addr`：TcpRoute2 对外监听地址（仅 socks5）。
- `UpStreams[].Name`：线路名称，仅用于日志显示。
- `UpStreams[].ProxyUrl`：代理线路 URL（不同协议写法不同，见后文）。
- `UpStreams[].DnsResolve`：是否本地先解析域名再连接。`socks4` 建议开启。
- `UpStreams[].Credit`：线路信誉分。
- `UpStreams[].Sleep`：该线路发起连接前延迟（毫秒）。
- `UpStreams[].CorrectDelay`：该线路延迟修正值（毫秒）。

## 2. ProxyUrl 通用规则

- URL 查询参数大小写不敏感（内部会转小写）。
- 多条线路并行竞争，系统自动选最快可用线路。
- `#备注` 可写在线路 URL 末尾（部分协议会读取）。
- URL 中的路径、特殊字符建议做百分号编码（例如 `/` -> `%2F`）。

### 2.1 级联代理 upProxy

对 `socks/http/https` 可用：

```text
socks5://a.b.c.d:1080?upProxy=http://u:p@1.2.3.4:8080
```

说明：

- 仅建议用于原生 `direct/socks/http/https` 链路。
- xray 协议线路（`shadowsocks/vmess/vless/trojan/wireguard/hysteria2`）不建议叠加 `upProxy`。

### 2.2 URL 内覆盖信用分

可以在 URL 里写 `credit` 覆盖 `UpStreams[].Credit`：

```text
socks5://1.2.3.4:1080?credit=10
```

## 3. 原生协议

## 3.1 direct

格式：

```text
direct://0.0.0.0:0000?localAddr=192.168.1.10:0
```

参数：

- `localAddr`：本地出口地址（可选），默认 `:0`。

## 3.2 socks4 / socks4a / socks5

格式：

```text
socks4://host:port
socks4a://host:port
socks5://host:port
```

说明：

- `socks4` 不支持远端域名解析，建议 `DnsResolve=true`。
- 当前 `socks5` 按无认证方式使用（不配置用户名密码）。

## 3.3 http / https

格式：

```text
http://host:port
http://user:pass@host:port
https://host:port
https://user:pass@host:port
```

说明：

- 支持 Basic 认证（`user:pass@`）。

## 4. xray 协议

说明：

- 本节协议由 `ProxyClient` 内置 `xray-core` 处理。
- 当前已接入：`shadowsocks`、`vmess`、`vless`、`trojan`、`wireguard`、`hysteria2`（兼容 `ss`、`hysteria`、`hy2`）。
- 本节协议统一支持两种输入形式：
  - 原始 URL 形式。
  - base64 形式：`协议名://BASE64(原始URL去掉协议头后的内容)`。

## 4.1 shadowsocks

主 scheme 建议使用 `shadowsocks://`，兼容 `ss://`。

支持格式 A（推荐）：

```text
shadowsocks://method:password@host:port
```

支持格式 B（整段编码）：

```text
shadowsocks://BASE64(method:password@host:port)
```

可选参数：

- `uot=1` 或 `uot=true`：启用 UoT。

示例：

```text
shadowsocks://aes-256-gcm:mypassword@1.2.3.4:8388?uot=1#hk-ss
```

注意：

- 若密码有特殊字符，请先 URL 编码。

## 4.2 vmess

支持格式：

```text
vmess://UUID@host:port?net=ws&security=auto&tls=tls&host=example.com&path=%2Fws
vmess://BASE64(JSON)
vmess://BASE64(UUID@host:port?net=ws&security=auto&tls=tls&host=example.com&path=%2Fws)
```

常用 JSON 字段：

- `add`：服务器地址。
- `port`：端口。
- `id`：UUID。
- `aid`：alterId。
- `net`：传输层，如 `tcp/ws/grpc/http/h2/quic/kcp/xhttp`。
- `type`：伪装类型。
- `host`：Host。
- `path`：路径。
- `tls`：`true/tls/xtls/reality`。
- `sni`、`alpn`、`fp`、`pbk`、`sid`、`spx`、`flow`。
- `security`：加密方式（如 `auto`）。
- `xver`：xhttp 版本（如 `h2/h3`）。
- `skip_cert_verify`：是否跳过证书校验。

示例（明文 JSON，实际需 Base64 后放入 URL）：

```json
{
  "v": "2",
  "ps": "vmess-demo",
  "add": "example.com",
  "port": "443",
  "id": "11111111-1111-1111-1111-111111111111",
  "aid": "0",
  "net": "ws",
  "type": "none",
  "host": "example.com",
  "path": "/ws",
  "tls": "tls",
  "sni": "example.com",
  "alpn": "h2,http/1.1",
  "fp": "chrome"
}
```

## 4.3 vless

格式：

```text
vless://UUID@host:port?encryption=none&type=tcp&security=tls
vless://BASE64(UUID@host:port?encryption=none&type=tcp&security=tls)
```

常用参数：

- `encryption`：默认 `none`。
- `flow`。
- `type`：默认 `tcp`，常见 `ws/grpc/http/h2/xhttp`。
- `security`：`tls/xtls/reality`。
- `path`、`host`、`sni`、`alpn`、`fp`。
- `pbk`、`sid`、`spx`（reality 场景）。
- `serviceName`（grpc）。
- `allowInsecure=0/1`。

示例：

```text
vless://11111111-1111-1111-1111-111111111111@example.com:443?encryption=none&type=ws&security=tls&host=example.com&path=%2Fws&sni=example.com&alpn=h2
```

## 4.4 trojan

格式：

```text
trojan://password@host:port?security=tls&type=tcp
trojan://BASE64(password@host:port?security=tls&type=tcp)
```

常用参数：

- `flow`。
- `type`：默认 `tcp`，可 `ws/grpc/http/h2/xhttp`。
- `security`：默认 `tls`，可 `xtls/reality`。
- `path`、`host`、`sni`、`alpn`、`fp`。
- `pbk`、`sid`、`spx`。
- `serviceName`。
- `allowInsecure=0/1`（默认偏宽松）。

示例：

```text
trojan://mypassword@example.com:443?security=tls&type=ws&host=example.com&path=%2Ftrojan&sni=example.com&alpn=h2
```

## 4.5 wireguard

格式：

```text
wireguard://secretKey@host:port?publicKey=peerKey&address=172.16.0.2/32
wireguard://BASE64(secretKey@host:port?publicKey=peerKey&address=172.16.0.2/32)
```

必填：

- `secretKey`：可放在 `user`，也可查询参数 `secretKey/privateKey`。
- `publicKey`：对端公钥。
- `address`：本地地址（可多个，逗号分隔）。

常用可选参数：

- `allowedIPs`：默认 `0.0.0.0/0,::/0`。
- `endpoint`：覆盖 `host:port`。
- `keepAlive`、`mtu`、`workers`。
- `reserved`：必须 3 个整数，逗号分隔。
- `domainStrategy`：默认 `ForceIP`。

示例：

```text
wireguard://mySecretKey@engage.cloudflareclient.com:2408?publicKey=peerPublicKey&address=172.16.0.2/32&allowedIPs=0.0.0.0/0,::/0&keepAlive=25&mtu=1280
```

## 4.6 hysteria2（兼容 hysteria / hy2）

三种 scheme 都支持：

- `hysteria://`
- `hysteria2://`
- `hy2://`

格式：

```text
hysteria2://password@host:port?sni=example.com
hysteria2://BASE64(password@host:port?sni=example.com)
```

常用参数：

- 鉴权：`password` 或 `auth`（二者可互补）。
- `sni`、`alpn`、`fp/fingerprint`。
- `allowInsecure`/`insecure`/`skip-cert-verify`。
- `up_mbps`、`down_mbps`（默认 100/100）。
- `congestion`（默认 `bbr`）。
- `obfs`、`obfsParam`。
- `hopPorts`。
- `protocol`（默认 `udp`）。
- `useUdpExtension`（默认开启）。

示例：

```text
hysteria2://mypassword@example.com:443?sni=example.com&alpn=h3&insecure=0&up_mbps=50&down_mbps=200
```

## 5. 推荐配置模板

```toml
addr = "127.0.0.1:7070"

[[UpStreams]]
Name = "direct"
ProxyUrl = "direct://0.0.0.0:0000"
DnsResolve = true
Credit = 0
Sleep = 30
CorrectDelay = 0

[[UpStreams]]
Name = "socks5-home"
ProxyUrl = "socks5://192.168.1.2:1080"
DnsResolve = false
Credit = 10
Sleep = 0
CorrectDelay = 0

[[UpStreams]]
Name = "hy2-main"
ProxyUrl = "hysteria2://pass@example.com:443?sni=example.com&alpn=h3&insecure=0"
DnsResolve = false
Credit = 20
Sleep = 0
CorrectDelay = 0
```

## 6. 快速排错

- 检查配置是否可读：

```bash
./TcpRoute2 -config config.toml
```

- 检查版本：

```bash
./TcpRoute2 -version
```

- 常见错误：

- `addr 错误的格式`：`ProxyUrl` 格式非法。
- `invalid host:port`：主机端口写法错误。
- `missing ...`：缺少协议必填参数（如 `UUID/password/publicKey`）。
- 路径参数未编码：`path=/ws` 建议写成 `path=%2Fws`。
