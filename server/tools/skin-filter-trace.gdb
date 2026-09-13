set pagination off
set confirm off
set breakpoint pending on
set print thread-events off
set logging file C:\Aug2017\logs\gdb-skin-filter-trace.log
set logging overwrite on
set logging enabled on
set $rows = 0

break *0x140d2ee1c
commands
  silent
  printf "SET_MANAGER parsed error=%u manager=%p selected=%u current=%u editorCollection=%u slotType=%u slotId=%u target=%u gear=%u weapon=%u collectionHead=%p\n", $eax & 0xff, $rbx, *(unsigned int *)$rbx, *(unsigned int *)($rbx+8), *(unsigned int *)($rbx+0x180), *(unsigned int *)($rbx+0x184), *(unsigned int *)($rbx+0x188), *(unsigned int *)($rbx+0x18c), *(unsigned char *)($rbx+0x248), *(unsigned char *)($rbx+0x249), *(void **)($rbx+0x130)
  continue
end

break *0x140d2e7df
commands
  silent
  printf "SET_CURRENT applied manager=%p selected=%u current=%u currentItemsHead=%p\n", $r14, *(unsigned int *)$r14, *(unsigned int *)($r14+8), *(void **)($r14+0x38)
  continue
end

break *0x140d30ef0
commands
  silent
  printf "OPEN_GEAR manager=%p selected=%u current=%u editorCollection=%u\n", $rcx, *(unsigned int *)$rcx, *(unsigned int *)($rcx+8), *(unsigned int *)($rcx+0x180)
  continue
end

break *0x140d2f640
commands
  silent
  set $rows = 0
  printf "FILTER_BEGIN manager=%p requestedCollection=%u selected=%u current=%u editorCollection=%u slotType=%u slotId=%u target=%u gear=%u collectionHead=%p\n", $rcx, *(unsigned int *)$rdx, *(unsigned int *)$rcx, *(unsigned int *)($rcx+8), *(unsigned int *)($rcx+0x180), *(unsigned int *)($rcx+0x184), *(unsigned int *)($rcx+0x188), *(unsigned int *)($rcx+0x18c), *(unsigned char *)($rcx+0x248), *(void **)($rcx+0x130)
  continue
end

break *0x140d2f700
commands
  silent
  if $rows < 12
    printf "ROW[%u] category=%u guid=%llu account=%u flags=0x%02x categoryLookup=%p\n", $rows, *(unsigned int *)$rdi, *(unsigned long long *)($rdi+8), *(unsigned int *)($rdi+0x10), *(unsigned char *)($rdi+0x14), $rax
  end
  set $rows = $rows + 1
  continue
end

break *0x140d2f716
commands
  silent
  if $rows <= 12
    printf "  slotItemLookup=%p\n", $rax
  end
  continue
end

break *0x140d2f778
commands
  silent
  if $rows <= 12
    printf "  conversion=%p reward=%u aux=%u\n", $rax, *(unsigned int *)($rax+0x10), *(unsigned int *)($rax+0x1c)
  end
  continue
end

break *0x140d2f837
commands
  silent
  printf "FILTER_END visited=%u accepted=%u\n", $rows, *(unsigned int *)($rbp-8)
  detach
  quit
end

continue
