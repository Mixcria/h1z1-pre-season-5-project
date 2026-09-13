import java.io.*;
import java.nio.file.*;
import java.util.*;
import com.jpexs.decompiler.flash.SWF;
import com.jpexs.decompiler.flash.abc.ABC;
import com.jpexs.decompiler.flash.abc.types.MethodBody;
import com.jpexs.decompiler.flash.abc.types.Multiname;
import com.jpexs.decompiler.flash.abc.avm2.instructions.AVM2Instruction;

// Modify stock AVM2 instruction sites, retaining the original traits, private
// namespaces, constructors, event handlers and every unrelated method body.
public class CrateUnlockBytecode {
    static ABC abc;
    static Set<MethodBody> changed = new HashSet<>();
    static AVM2Instruction op(int code, int... operands) {
        return new AVM2Instruction(0, code, operands.length == 0 ? null : operands);
    }
    static void require(boolean condition, String message) {
        if (!condition) throw new IllegalStateException(message);
    }
    static String name(AVM2Instruction instruction) {
        if (instruction.operands == null || instruction.operands.length == 0) return "";
        return abc.constants.getString(abc.constants.getMultiname(instruction.operands[0]).name_index);
    }
    static int qname(String name) {
        int ns = abc.constants.getNamespaceId(0x16, "", 0, true);
        return abc.constants.getMultinameId(Multiname.createQName(false, abc.constants.getStringId(name, true), ns), true);
    }
    static void cap(MethodBody body, int expected) {
        List<Integer> sites = new ArrayList<>();
        for (int i = 0; i < body.getCode().code.size(); i++) {
            AVM2Instruction instruction = body.getCode().code.get(i);
            if (instruction.definition.instructionCode == 0x66 && name(instruction).equals("Quantity")) sites.add(i);
        }
        require(sites.size() == expected, "Unexpected Quantity sites: " + sites.size());
        Collections.reverse(sites);
        for (int site : sites) {
            body.insertAll(site + 1, List.of(op(0x60, qname("Math")), op(0x2b), op(0x24, 10), op(0x46, qname("min"), 2)));
        }
        body.max_stack += 2;
        body.setModified();
        changed.add(body);
    }
    static MethodBody method(String cls, String method) {
        MethodBody result = null;
        String simple = cls.substring(cls.lastIndexOf('.') + 1);
        for (var instance : abc.instance_info) {
            if (!abc.constants.getString(abc.constants.getMultiname(instance.name_index).name_index).equals(simple)) continue;
            for (var trait : instance.instance_traits.traits) {
                if (trait instanceof com.jpexs.decompiler.flash.abc.types.traits.TraitMethodGetterSetter named &&
                    abc.constants.getString(abc.constants.getMultiname(trait.name_index).name_index).equals(method)) {
                    require(result == null, "Duplicate method " + cls + "." + method);
                    result = abc.findBody(named.method_info);
                }
            }
        }
        require(result != null, "Missing method " + cls + "." + method);
        System.out.println(cls + "." + method + " method=" + result.method_info + " body=" + abc.bodies.indexOf(result));
        return result;
    }
    public static void main(String[] args) throws Exception {
        SWF swf;
        try (InputStream in = Files.newInputStream(Path.of(args[0]))) { swf = new SWF(in, false); }
        require(swf.getAbcList().size() == 1, "Expected one ABC tag");
        abc = swf.getAbcList().get(0).getABC();
        Map<MethodBody, byte[]> originals = new IdentityHashMap<>();
        for (MethodBody body : abc.bodies) originals.put(body, body.getCodeBytes().clone());
        cap(method("views.marketplace.managers.MarketplaceManager", "purchaseCallback"), 1);
        cap(method("views.marketplace.managers.MarketplaceManager", "getpriceFromData"), 1);
        cap(method("views.marketplace.BuyButton", "draw"), 2);

        MethodBody config = method("views.marketplace.pages.store.AccountDetailPanel", "configUI");
        int found = 0;
        for (int i = config.getCode().code.size() - 2; i >= 1; i--) {
            AVM2Instruction instruction = config.getCode().code.get(i);
            if (instruction.definition.instructionCode != 0x2c || !abc.constants.getString(instruction.operands[0]).equals("UI.CharacterMenu.AccountInventory.UnlockAll")) continue;
            require(config.getCode().code.get(i - 1).definition.instructionCode == 0x60, "Expected locale receiver");
            require(config.getCode().code.get(i + 1).definition.instructionCode == 0x46 && name(config.getCode().code.get(i + 1)).equals("translateCodeString"), "Expected locale translation");
            config.removeInstruction(i + 1);
            config.replaceInstruction(i, op(0x2c, abc.constants.getStringId("Unlock 10", true)));
            config.removeInstruction(i - 1);
            found++;
        }
        require(found == 1, "Expected one old label");
        config.setModified(); changed.add(config);

        MethodBody draw = method("views.marketplace.pages.store.AccountDetailPanel", "draw");
        found = 0;
        for (int i = draw.getCode().code.size() - 1; i >= 4; i--) {
            AVM2Instruction instruction = draw.getCode().code.get(i);
            if (instruction.definition.instructionCode != 0x4f || !name(instruction).equals("setItem") || instruction.operands[1] != 2) continue;
            List<AVM2Instruction> code = draw.getCode().code;
            require(code.get(i - 1).definition.instructionCode == 0x26 && name(code.get(i - 2)).equals("m_itemData") && name(code.get(i - 4)).equals("m_unlockStackButton"), "Unexpected stack button setup");
            int buttonName = code.get(i - 4).operands[0], itemName = code.get(i - 2).operands[0];
            draw.insertAll(i - 5, List.of(op(0xd0), op(0x66, buttonName), op(0x2c, abc.constants.getStringId("Unlock ", true)),
                op(0x60, qname("Math")), op(0x24, 10), op(0xd0), op(0x66, itemName), op(0x66, qname("Quantity")),
                op(0x46, qname("min"), 2), op(0xa0), op(0x61, qname("label"))));
            found++;
        }
        require(found == 1, "Expected one stack button setup");
        draw.max_stack = Math.max(draw.max_stack, 5);
        draw.setModified(); changed.add(draw);
        require(changed.size() == 5, "Expected exactly five edited methods");
        int unchanged = 0;
        for (MethodBody body : abc.bodies) {
            if (!changed.contains(body)) {
                require(Arrays.equals(originals.get(body), body.getCodeBytes()), "Unrelated method changed: " + body.method_info);
                unchanged++;
            }
        }
        System.out.println("Unchanged method bytecode: " + unchanged + "; changed: " + changed.size());
        ((com.jpexs.decompiler.flash.tags.Tag)swf.getAbcList().get(0)).setModified(true);
        try (OutputStream out = Files.newOutputStream(Path.of(args[1]))) { swf.saveTo(out); }
    }
}
