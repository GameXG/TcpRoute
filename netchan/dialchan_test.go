package netchan

import (
	"github.com/GameXG/ProxyClient"
	"testing"
	"time"
)

func TextChanDialTimeout(t *testing.T) {
	connChan := make(chan ConnRes)
	exitChan := make(chan int)
	defer close(exitChan)

	dial, err := proxyclient.NewProxyClient("direct://0.0.0.0:0000/")
	if err != nil {
		panic(err)
	}

	go func() {
		ChanDialTimeout(dial, 0, connChan, exitChan, true, "user data 111", nil, "tcp", "www.baidu.com:80", 5*time.Second)
		close(connChan)
		close(exitChan)
	}()

	i := 0

	for _ = range connChan {
		i++
	}

	if i == 0 {
		t.Errorf("错误，未成功建立至少1个连接。")
	}

}
