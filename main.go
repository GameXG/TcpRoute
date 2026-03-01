// TcpRoute2 project main.go
package main

import (
	"flag"
	"fmt"
	"github.com/koding/multiconfig"
	"time"
)

const version = "0.1.3"

type ServerConfig struct {
	Addr      string `default:":5050"`
	UpStreams []ServerConfigUpStream
}

type ServerConfigUpStream struct {
	Name         string `default:"direct"`
	ProxyUrl     string `default:"direct://0.0.0.0:0000"`
	DnsResolve   bool   `default:"false"`
	Credit       int    `default:"0"`
	Sleep        int    `default:"0"`
	CorrectDelay int    `default:"0"`
}

func main() {
	printVer := flag.Bool("version", false, "print version")
	config_path := flag.String("config", "config.toml", "配置文件路径")
	flag.Parse()

	if *printVer {
		fmt.Println("TcpRoute2 version", version)
		return
	}

	m := multiconfig.NewWithPath(*config_path)

	serverConfig := new(ServerConfig)
	m.MustLoad(serverConfig)

	// 创建 tcpping 上层代理
	upStream := NewTcppingUpStream()

	for _, up := range serverConfig.UpStreams {
		if err := upStream.AddUpStream(up.Name, up.ProxyUrl, up.DnsResolve, up.Credit, time.Duration(up.Sleep)*time.Millisecond, time.Duration(up.CorrectDelay)*time.Millisecond); err != nil {
			panic(err)
		}
	}

	// 服务器监听
	srv := NewServer(serverConfig.Addr, upStream)

	// DNS 配置

	// 各端口需要的安全级别

	srv.ListAndServe()
}
