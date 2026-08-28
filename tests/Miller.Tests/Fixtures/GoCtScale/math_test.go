package goctscale

import "testing"

func Test1(t *testing.T) {
	if 1 != 1 {
		t.Fatal("number-named test failed")
	}
}

func TestAdd(t *testing.T) {
	if 1+1 != 2 {
		t.Fatal("addition failed")
	}
}

func TestSub(t *testing.T) {
	if 3-1 != 2 {
		t.Fatal("subtraction failed")
	}
}
