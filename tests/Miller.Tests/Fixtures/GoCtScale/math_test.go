package goctscale

import "testing"

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
