package first

import "testing"

func TestFirst(t *testing.T) {
	if 2+2 != 4 {
		t.Fatal("first module failed")
	}
}
