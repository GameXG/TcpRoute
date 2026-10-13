package main

import (
	"fmt"
	"github.com/GameXG/ProxyClient"
	"github.com/GameXG/TcpRoute2/netchan"
	"time"
)

func main() {
	connChan := make(chan netchan.ConnRes)
	exitChan := make(chan int)
	defer close(exitChan)

	dial, err := proxyclient.NewProxyClient("direct://0.0.0.0:0000/")
	if err != nil {
		panic(err)
	}

	go func() {
		netchan.ChanDialTimeout(dial, 0, connChan, exitChan, true, nil, nil, "tcp", "www.163.com:80", 5*time.Second)
		close(connChan)
	}()

	for c := range connChan {
		fmt.Printf("目标 %v 耗时 %v 。\r\n", c.Conn.RemoteAddr(), c.Ping)
	}

}
