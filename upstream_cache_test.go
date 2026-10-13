package main

import (
	"testing"
	"time"
)

func TestUpStreamCache(t *testing.T) {
	cache := NewUpStreamConnCache(nil)

	_, err := cache.GetOptimal("www.163.com:80")
	if err == nil {
		t.Error("错误")
	}

	cache.Updata("www.163.com:80", "1.2.3.4:80", 3*time.Millisecond, nil, "t1")

	item, err := cache.GetOptimal("www.163.com:80")
	if err != nil {
		t.Error("错误")
	}

	if item.dialClient != nil || item.dialName != "t1" || item.DomainAddr != "www.163.com:80" || item.IpAddr != "1.2.3.4:80" || item.TcpPing != 3*time.Millisecond {
		t.Error("错误")
	}

	// 增加一个更快的连接
	cache.Updata("www.163.com:80", "2.3.4.5:80", 2*time.Millisecond, nil, "t1")
	item2, err := cache.GetOptimal("www.163.com:80")
	if err != nil {
		t.Error("错误")
	}
	if item2.dialClient != nil || item2.dialName != "t1" || item2.DomainAddr != "www.163.com:80" || item2.IpAddr != "2.3.4.5:80" || item2.TcpPing != 2*time.Millisecond {
		t.Error("错误")
	}

	// 更新一个现存的连接速度
	cache.Updata("www.163.com:80", "1.2.3.4:80", 1*time.Millisecond, nil, "t1")
	item3, err := cache.GetOptimal("www.163.com:80")
	if err != nil {
		t.Error("错误")
	}
	if item3.dialClient != nil || item3.dialName != "t1" || item3.DomainAddr != "www.163.com:80" || item3.IpAddr != "1.2.3.4:80" || item3.TcpPing != 1*time.Millisecond {
		t.Error("错误")
	}

	// 增加一个更快的连接
	cache.Updata("www.163.com:80", "2.3.4.5:80", 0*time.Millisecond, nil, "t2")
	item4, err := cache.GetOptimal("www.163.com:80")
	if err != nil {
		t.Error("错误")
	}
	if item4.dialClient != nil || item4.dialName != "t2" || item4.DomainAddr != "www.163.com:80" || item4.IpAddr != "2.3.4.5:80" || item4.TcpPing != 0*time.Millisecond {
		t.Error("错误")
	}

}
