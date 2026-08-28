package second

import "testing"

func TestSecond(t *testing.T) {
	if 6/2 != 3 {
		t.Fatal("second module failed")
	}
}
