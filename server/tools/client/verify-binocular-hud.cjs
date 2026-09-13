// Execute the changed method bodies exported from the built GFx, rather than a
// second implementation of their logic. JavaScript shares these AS3 coercions.
const fs = require('node:fs');
const path = require('node:path');
const assert = require('node:assert/strict');
const dir = process.argv[2];
if (!dir) throw new Error('Usage: node verify-binocular-hud.cjs <build-directory>');
function method(file, signature) {
    const source = fs.readFileSync(path.join(dir, file), 'utf8');
    const start = source.indexOf(signature);
    assert.ok(start >= 0, 'Missing method ' + signature);
    const begin = source.indexOf('{', start);
    let depth = 1, end = begin + 1;
    while (depth && end < source.length) {
        if (source[end] === '{') depth++;
        if (source[end] === '}') depth--;
        end++;
    }
    assert.equal(depth, 0);
    const body = source.slice(begin + 1, end - 1);
    assert.ok(!body.includes('//unpopped'), 'Invalid decompiled bytecode stack');
    return body;
}
const ammo = new Function('GetData', method('ammo/verify/scripts/ui/datasource/rows/CurrentLoadoutRow.as',
                                          'function get weaponShouldShowAmmo()'));
for (const value of ['0', '', null, undefined, 'false'])
    assert.equal(ammo(() => value), false, 'Hide ammo for ' + value);
assert.equal(ammo(() => '1'), true, 'Firearms still show ammo');
console.log('Binocular HUD: ammo coercion regressions passed.');
