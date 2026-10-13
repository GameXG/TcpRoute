package main

import (
	"testing"
)

func TestErrConn(t *testing.T) {
	e := NewErrConnService()

	e.AddErrLog("d1", "www.163.com:80", "1.2.3.4:80", ErrConnTypeRead0)
	e.AddErrLog("d2", "www.163.com:80", "1.2.3.5:80", ErrConnTypeRead0)
	e.AddErrLog("d3", "www.163.com:80", "1.2.3.6:80", ErrConnTypeRead0)
	e.AddErrLog("d3", "www.163.com:80", "1.2.3.4:80", ErrConnTypeRead0)
	e.AddErrLog("d3", "www.163.com:80", "1.2.3.4:80", ErrConnTypeRead0)
	e.AddErrLog("d3", "www.163.com:80", "1.2.3.4:80", ErrConnTypeRead0)
	e.AddErrLog("d3", "www.163.com:80", "1.2.3.4:80", ErrConnTypeRead0)
	e.AddErrLog("d4", "www.163.com:80", "www.163.com:80", ErrConnTypeRead0)
	e.AddErrLog("d5", "www.163.com:80", "www.163.com:80", ErrConnTypeRead0)

	if e.Check("d1", "www.163.com:80", "1.2.3.4:80") != false {
		t.Error("错误")
	}

	if e.Check("d3", "www.163.com:80", "1.2.3.0:80") != false {
		t.Error("错误")
	}

	if e.Check("d5", "www.163.com:80", "1.2.3.0:80") != true {
		t.Error("错误")
	}

	if e.Check("d6", "www.163.com:80", "www.163.com:80") != true {
		t.Error("错误")
	}

}
