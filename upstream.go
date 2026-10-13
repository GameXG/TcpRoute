package main

import (
	"net"
	"time"
)

/*

为了实现 http 、https 连接异常自动切换连接IP的功能，必须接受连接并读取请求内容。

大概和标准的 tcp 连接一样即可

先尝试建立连接，对于非 http、https 是真正的尝试建立连接，建立连接后才返回建立连接成功。

对于 http、https 连接是直接返回建立连接成功，然后接收 http 请求内容，根据http 请求的 host 或 https 的 SNI 来获得目标网站的地址。这样可以防止dns欺骗。


*/

type UpStream interface {
}

// 基本的错误报告实现
type UpStreamErrorReportingBase struct {
	errConnServer                *ErrConnService
	DailName, DomainAddr, IpAddr string
}

func (er *UpStreamErrorReportingBase) Report(t ErrConnType) {
	er.errConnServer.AddErrLog(er.DailName, er.DomainAddr, er.IpAddr, t)
}

// dial 提供的错误报告接口
// 当 dial 调用者认为链接有问题时将调用这个接口向 连接提供者报告错误。
type UpStreamErrorReporting interface {
	// 使用者认为连接有问题时调用
	Report(t ErrConnType)
}

type UpStreamDial interface {
	// 建立新连接
	// 如果没有实现 UpStreamErrorReporting 可以返回 nil
	DialTimeout(network, address string, timeout time.Duration) (net.Conn, UpStreamErrorReporting, error)
}
