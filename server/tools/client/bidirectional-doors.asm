
pushfq
push rax
push r10
push r11
sub rsp, 0x10
movdqu [rsp], xmm0
mov rax, [rsp+0x58]
test rax, rax
jz done
mov rax, [rax]
mov r11, 0x4352444f4f523100
xor r11, rax
cmp r11, 1
ja done
test byte ptr [r8+6], 1
jz done
test byte ptr [r9+6], 1
jnz done
test byte ptr [rcx+0x18de], 1
jnz done
cmp dword ptr [rcx+0x4348], 0
jle done
mov r10, [rcx+0xd10]
test r10, r10
jz done
mov r11, 0x1122334455667788
cmp [r10], r11
jne done
cmp [r10+8], rcx
jne done
movss xmm0, [r10+0x68]
mov r11d, 0x3fc90fdb
test al, 1
jz positive
or r11d, 0x80000000
positive:
push r11
addss xmm0, [rsp]
add rsp, 8
movss [r10+0x64], xmm0
mov byte ptr [r10+0x50], 0
done:
movdqu xmm0, [rsp]
add rsp, 0x10
pop r11
pop r10
pop rax
popfq
mov [rsp+0x18], rbx
