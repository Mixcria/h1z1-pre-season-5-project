set pagination off
set confirm off
set print thread-events off

set $client = *(unsigned long long *)0x143f69f60
set $manager = $client + 0xf2f8
printf "client=%p manager=%p\n", $client, $manager
printf "selected=%u current=%u\n", *(unsigned int *)$manager, *(unsigned int *)($manager + 8)
printf "editorCollection=%u slotType=%u slotId=%u targetPrototype=%u emoteSlot=%u\n", *(unsigned int *)($manager + 0x180), *(unsigned int *)($manager + 0x184), *(unsigned int *)($manager + 0x188), *(unsigned int *)($manager + 0x18c), *(unsigned int *)($manager + 0x190)
printf "gearActive=%u weaponActive=%u\n", *(unsigned char *)($manager + 0x248), *(unsigned char *)($manager + 0x249)
printf "worn-list/list-object at +0x28\n"
x/24gx $manager + 0x28
printf "collection-map/list-object at +0x120\n"
x/40gx $manager + 0x120
detach
