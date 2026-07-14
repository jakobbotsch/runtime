// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#include "jitpch.h"

class MorphInitBlockHelper
{
public:
    static GenTree* MorphInitBlock(Compiler* comp, GenTree* tree);

protected:
    MorphInitBlockHelper(Compiler* comp, GenTree* store, bool initBlock);

    GenTree* Morph();

    void         PrepareDst();
    virtual void PrepareSrc();

    virtual void TrySpecialCases();
    virtual void MorphStructCases();

    void PropagateBlockAssertions();
    void PropagateExpansionAssertions();

    virtual const char* GetHelperName() const
    {
        return "MorphInitBlock";
    }

private:
    void     TryInitFieldByField();
    void     TryPrimitiveInit();
    GenTree* EliminateCommas(GenTree** commaPool);

protected:
    Compiler* m_compiler;
    bool      m_initBlock;

    GenTree* m_store = nullptr;
    GenTree* m_src   = nullptr;

    unsigned             m_blockSize            = 0;
    ClassLayout*         m_blockLayout          = nullptr;
    unsigned             m_dstLclNum            = BAD_VAR_NUM;
    GenTreeLclVarCommon* m_dstLclNode           = nullptr;
    LclVarDsc*           m_dstVarDsc            = nullptr;
    unsigned             m_dstLclOffset         = 0;
    bool                 m_dstSingleStoreLclVar = false;

    enum class BlockTransformation
    {
        Undefined,
        FieldByField,
        OneStoreBlock,
        StructBlock,
        SkipMultiRegSrc,
        SkipSingleRegCallSrc,
        Nop
    };

    BlockTransformation m_transformationDecision = BlockTransformation::Undefined;
    GenTree*            m_result                 = nullptr;
};

//------------------------------------------------------------------------
// MorphInitBlock: Morph a block initialization store tree.
//
// Arguments:
//    comp - a compiler instance;
//    tree - A store tree that performs block initialization.
//
// Return Value:
//    A possibly modified tree to perform the initialization.
//
// static
GenTree* MorphInitBlockHelper::MorphInitBlock(Compiler* comp, GenTree* tree)
{
    const bool           initBlock = true;
    MorphInitBlockHelper helper(comp, tree, initBlock);
    return helper.Morph();
}

//------------------------------------------------------------------------
// MorphInitBlockHelper: helper's constructor.
//
// Arguments:
//    comp - a compiler instance;
//    initBlock - true if this is init block op, false if it is a copy block;
//    store - store node to morph.
//
// Notes:
//    Most class members are initialized via in-class member initializers.
//
MorphInitBlockHelper::MorphInitBlockHelper(Compiler* comp, GenTree* store, bool initBlock = true)
    : m_compiler(comp)
    , m_initBlock(initBlock)
{
    assert(store->OperIsStore());
    assert((m_initBlock == store->OperIsInitBlkOp()) && (!m_initBlock == store->OperIsCopyBlkOp()));
    m_store = store;
}

//------------------------------------------------------------------------
// Morph: transform the store to a possible better form and changes its
//    operands to an appropriate form for later phases.
//
// Return Value:
//    A possibly modified tree to perform the block operation.
//
// Notes:
//    It is used for both init and copy block.
//
GenTree* MorphInitBlockHelper::Morph()
{
    JITDUMP("%s:\n", GetHelperName());

    GenTree* commaPool;
    GenTree* sideEffects = EliminateCommas(&commaPool);

    PrepareDst();
    PrepareSrc();
    PropagateBlockAssertions();
    TrySpecialCases();

    if (m_transformationDecision == BlockTransformation::Undefined)
    {
        MorphStructCases();
    }

    PropagateExpansionAssertions();

    assert(m_transformationDecision != BlockTransformation::Undefined);
    assert(m_result != nullptr);

    m_result->SetMorphed(m_compiler);

    while (sideEffects != nullptr)
    {
        if (commaPool != nullptr)
        {
            GenTree* comma = commaPool;
            commaPool      = commaPool->gtNext;

            assert(comma->OperIs(GT_COMMA));
            comma->gtType        = TYP_VOID;
            comma->AsOp()->gtOp1 = sideEffects;
            comma->AsOp()->gtOp2 = m_result;
            comma->gtFlags       = (sideEffects->gtFlags | m_result->gtFlags) & GTF_ALL_EFFECT;

            m_result = comma;
        }
        else
        {
            m_result = m_compiler->gtNewOperNode(GT_COMMA, TYP_VOID, sideEffects, m_result);
        }
        m_result->SetMorphed(m_compiler);
        sideEffects = sideEffects->gtNext;
    }

    JITDUMP("%s (after):\n", GetHelperName());
    DISPTREE(m_result);

    return m_result;
}

//------------------------------------------------------------------------
// PrepareDst: Initialize member fields with information about the store's
//    destination.
//
void MorphInitBlockHelper::PrepareDst()
{
    if (m_store->OperIsLocalStore())
    {
        m_dstLclNode   = m_store->AsLclVarCommon();
        m_dstLclOffset = m_dstLclNode->GetLclOffs();
        m_dstLclNum    = m_dstLclNode->GetLclNum();
        m_dstVarDsc    = m_compiler->lvaGetDesc(m_dstLclNum);

        // Kill everything about m_dstLclNum (and its field locals)
        if (m_compiler->optLocalAssertionProp && (m_compiler->optAssertionCount > 0))
        {
            m_compiler->fgKillDependentAssertions(m_dstLclNum DEBUGARG(m_store));
        }
    }
    else
    {
        assert(m_store->OperIs(GT_STOREIND, GT_STORE_BLK));
    }

    if (m_store->TypeIs(TYP_STRUCT))
    {
        m_blockLayout = m_store->GetLayout(m_compiler);
        m_blockSize   = m_blockLayout->GetSize();
    }
    else
    {
        m_blockSize = genTypeSize(m_store);
    }

    assert(m_blockSize != 0);

#if defined(DEBUG)
    if (m_compiler->verbose)
    {
        printf("PrepareDst for [%06u] ", m_compiler->dspTreeID(m_store));
        if (m_dstLclNode != nullptr)
        {
            printf("have found a local var V%02u.\n", m_dstLclNum);
        }
        else
        {
            printf("have not found a local var.\n");
        }
    }
#endif // DEBUG
}

//------------------------------------------------------------------------
// PropagateBlockAssertions: propagate assertions based on the original tree
//
// Notes:
//    Once the init or copy tree is morphed, assertion gen can no
//    longer recognize what it means.
//
//    So we generate assertions based on the original tree.
//
void MorphInitBlockHelper::PropagateBlockAssertions()
{
    if (m_compiler->optLocalAssertionProp)
    {
        m_compiler->fgAssertionGen(m_store);
    }
}

//------------------------------------------------------------------------
// PropagateExpansionAssertions: propagate assertions based on the
//   expanded tree
//
// Notes:
//    After the copy/init is expanded, we may see additional assertions
//    to generate.
//
void MorphInitBlockHelper::PropagateExpansionAssertions()
{
    if (m_compiler->optLocalAssertionProp && (m_transformationDecision == BlockTransformation::OneStoreBlock))
    {
        m_compiler->fgAssertionGen(m_store);
    }
}

//------------------------------------------------------------------------
// PrepareSrc: Initialize member fields with information about the store's
//    source value.
//
void MorphInitBlockHelper::PrepareSrc()
{
    m_src = m_store->Data();
}

//------------------------------------------------------------------------
// TrySpecialCases: check special cases that require special transformations.
//    We don't have any for init block.
//
void MorphInitBlockHelper::TrySpecialCases()
{
    return;
}

//------------------------------------------------------------------------
// MorphStructCases: transforms the store as field by field init or keeps it as a block init
//    but sets appropriate flags for the involved lclVars.
//
// Assumptions:
//    we have already checked that it is not a special case.
//
void MorphInitBlockHelper::MorphStructCases()
{
    if (m_transformationDecision == BlockTransformation::Undefined)
    {
        TryPrimitiveInit();
    }

    if (m_transformationDecision == BlockTransformation::Undefined)
    {
        m_result                 = m_store;
        m_transformationDecision = BlockTransformation::StructBlock;

        if (m_dstVarDsc != nullptr)
        {
            if (m_store->OperIs(GT_STORE_LCL_FLD))
            {
                m_compiler->lvaSetVarDoNotEnregister(m_dstLclNum DEBUGARG(DoNotEnregisterReason::LocalField));
            }
        }
    }
}

//------------------------------------------------------------------------
// InitFieldByField: Attempts to promote a local block init tree to a tree
// of promoted field initialization stores.
//
// If successful, will set "m_transformationDecision" to "FieldByField" and
// "m_result" to the final tree.
//
// Notes:
//    This transforms a single block initialization store like:
//
//    *  STORE_BLK struct<12> (init)
//    +--*  LCL_ADDR  byref V02 loc0
//    |  \--  int    V02.a (offs=0x00) -> V06 tmp3
//    |  \--  ubyte  V02.c (offs=0x04) -> V07 tmp4
//    |  \--  float  V02.d (offs=0x08) -> V08 tmp5
//    \--*  INIT_VAL  int
//       \--*  CNS_INT   int    42
//
//    into a COMMA tree of stores that initialize each promoted struct
//    field:
//
//    *  COMMA     void
//    +--*  COMMA     void
//    |  +--*  STORE_LCL_VAR int    V06 tmp3
//    |  |  \--*  CNS_INT   int    0x2A2A2A2A
//    |  \--*  STORE_LCL_VAR ubyte  V07 tmp4
//    |     \--*  CNS_INT   int    42
//    \--*  STORE_LCL_VAR float  V08 tmp5
//       \--*  CNS_DBL   float  1.5113661732714390e-13
//
void MorphInitBlockHelper::TryInitFieldByField()
{
    unreached();
}

//------------------------------------------------------------------------
// TryPrimitiveInit: Replace block zero-initialization with a primitive store.
//
// Transforms patterns like "STORE_BLK(LCL_VAR_ADDR, 0)" into simple
// stores: "STORE_LCL_VAR<int>(0)".
//
// If successful, will set "m_transformationDecision" to "OneStoreBlock".
//
void MorphInitBlockHelper::TryPrimitiveInit()
{
    if (m_src->IsIntegralConst(0) && (m_dstVarDsc != nullptr) && (genTypeSize(m_dstVarDsc) == m_blockSize))
    {
        var_types lclVarType = m_dstVarDsc->TypeGet();
        if (varTypeIsSIMD(lclVarType))
        {
            m_src = m_compiler->gtNewZeroConNode(lclVarType);
        }
        else
        {
            m_src->BashToZeroConst(lclVarType);
        }

        m_store->ChangeType(m_dstVarDsc->lvNormalizeOnLoad() ? lclVarType : genActualType(lclVarType));
        m_store->ChangeOper(GT_STORE_LCL_VAR);
        m_store->AsLclVar()->SetLclNum(m_dstLclNum);
        m_store->gtFlags |= GTF_VAR_DEF;

        m_result                 = m_store;
        m_transformationDecision = BlockTransformation::OneStoreBlock;
    }
}

//------------------------------------------------------------------------
// EliminateCommas: Prepare for block morphing by removing commas from the
// source operand of the store.
//
// Parameters:
//   commaPool - [out] Pool of GT_COMMA nodes linked by their gtNext nodes that
//                     can be used by the caller to avoid unnecessarily creating
//                     new commas.
//
// Returns:
//   Extracted side effects, in reverse order, linked via the gtNext fields of
//   the nodes.
//
// Notes:
//   We have a tree like the following:
//
//         STOREIND
//         /      \.
//        B      COMMA
//                /  \.
//               C    D
//
//   We'd like downstream code to just see and expand STOREIND(B, D).
//   We will produce:
//
//                     COMMA
//                   /       \.
//    STORE_LCL_VAR<tmp>    COMMA
//           /              /   \.
//          B              C  STOREIND
//                             /  \.
//                           tmp   D
//
//   If the store has GTF_REVERSE_OPS then we will produce:
//
//     COMMA
//     /   \.
//    C   STOREIND
//         /  \.
//        B    D
//
//   While keeping the GTF_REVERSE_OPS.
//
//   Note that the final resulting tree is created in the caller since it also
//   needs to propagate side effect flags from the decomposed store to all the
//   created commas. Therefore this function just returns a linked list of the
//   side effects to be used for that purpose.
//
GenTree* MorphInitBlockHelper::EliminateCommas(GenTree** commaPool)
{
    *commaPool = nullptr;

    GenTree* sideEffects   = nullptr;
    auto     addSideEffect = [&sideEffects](GenTree* sideEff) {
        sideEff->gtNext = sideEffects;
        sideEffects     = sideEff;
    };

    auto addComma = [commaPool, &addSideEffect](GenTree* comma) {
        addSideEffect(comma->gtGetOp1());
        comma->gtNext = *commaPool;
        *commaPool    = comma;
    };

    GenTree* src = m_store->Data();

    if (m_store->IsReverseOp())
    {
        while (src->OperIs(GT_COMMA))
        {
            addComma(src);
            src = src->gtGetOp2();
        }
    }
    else
    {
        if (m_store->OperIsIndir() && src->OperIs(GT_COMMA))
        {
            GenTree* addr = m_store->AsIndir()->Addr();
            if (((addr->gtFlags & GTF_ALL_EFFECT) != 0) || (((src->gtFlags & GTF_ASG) != 0) && !addr->IsInvariant()))
            {
                unsigned lhsAddrLclNum = m_compiler->lvaGrabTemp(true DEBUGARG("Block morph LHS addr"));

                GenTree* const tempStore = m_compiler->gtNewTempStore(lhsAddrLclNum, addr);
                tempStore->SetMorphed(m_compiler);
                addSideEffect(tempStore);
                GenTree* const tempRead = m_compiler->gtNewLclvNode(lhsAddrLclNum, genActualType(addr));
                tempRead->SetMorphed(m_compiler);
                m_store->AsUnOp()->gtOp1 = tempRead;
                m_compiler->gtUpdateNodeSideEffects(m_store);
            }
        }

        while (src->OperIs(GT_COMMA))
        {
            addComma(src);
            src = src->gtGetOp2();
        }
    }

    if (sideEffects != nullptr)
    {
        m_store->Data() = src;
        m_compiler->gtUpdateNodeSideEffects(m_store);
    }

    return sideEffects;
}

class MorphCopyBlockHelper : public MorphInitBlockHelper
{
public:
    static GenTree* MorphCopyBlock(Compiler* comp, GenTree* tree);

protected:
    MorphCopyBlockHelper(Compiler* comp, GenTree* store);

    void PrepareSrc() override;

    void TrySpecialCases() override;

    void     MorphStructCases() override;
    void     TryPrimitiveCopy();
    GenTree* CopyFieldByField();

    const char* GetHelperName() const override
    {
        return "MorphCopyBlock";
    }

protected:
    unsigned             m_srcLclNum            = BAD_VAR_NUM;
    LclVarDsc*           m_srcVarDsc            = nullptr;
    GenTreeLclVarCommon* m_srcLclNode           = nullptr;
    unsigned             m_srcLclOffset         = 0;
    bool                 m_srcSingleStoreLclVar = false;

    bool m_dstDoFldStore = false;
    bool m_srcDoFldStore = false;

private:
    bool CanReuseAddressForDecomposedStore(GenTree* addr);
};

//------------------------------------------------------------------------
// MorphCopyBlock: Morph a block copy tree.
//
// Arguments:
//    comp - a compiler instance;
//    tree - A store tree that performs block copy.
//
// Return Value:
//    A possibly modified tree to perform the copy.
//
// static
GenTree* MorphCopyBlockHelper::MorphCopyBlock(Compiler* comp, GenTree* tree)
{
    MorphCopyBlockHelper helper(comp, tree);
    return helper.Morph();
}

//------------------------------------------------------------------------
// MorphCopyBlockHelper: helper's constructor.
//
// Arguments:
//    comp - a compiler instance;
//    store - store node to morph.
//
// Notes:
//    Most class members are initialized via in-class member initializers.
//
MorphCopyBlockHelper::MorphCopyBlockHelper(Compiler* comp, GenTree* store)
    : MorphInitBlockHelper(comp, store, false)
{
}

//------------------------------------------------------------------------
// PrepareSrc: Initialize member fields with information about the store's
//    source value.
//
void MorphCopyBlockHelper::PrepareSrc()
{
    m_src = m_store->Data();

    if (m_src->IsLocal())
    {
        m_srcLclNode   = m_src->AsLclVarCommon();
        m_srcLclOffset = m_srcLclNode->GetLclOffs();
        m_srcLclNum    = m_srcLclNode->GetLclNum();
        m_srcVarDsc    = m_compiler->lvaGetDesc(m_srcLclNum);
    }

    // Verify that the types of the store and data match.
    assert(m_store->TypeGet() == m_src->TypeGet());
    if (m_store->TypeIs(TYP_STRUCT))
    {
        assert(m_blockLayout->CanAssignFrom(m_src->GetLayout(m_compiler)));
    }
}

// TrySpecialCases: check special cases that require special transformations.
//    The current special cases include stores with calls as values.
//
void MorphCopyBlockHelper::TrySpecialCases()
{
    if (m_src->IsMultiRegNode())
    {
        assert(m_store->OperIs(GT_STORE_LCL_VAR));

        m_dstVarDsc->SetIsMultiRegDest();

        JITDUMP("Not morphing a multireg node return\n");
        m_transformationDecision = BlockTransformation::SkipMultiRegSrc;
        m_result                 = m_store;
    }
    else if (m_src->IsCall() && m_store->OperIs(GT_STORE_LCL_VAR) && m_dstVarDsc->CanBeReplacedWithItsField(m_compiler))
    {
        JITDUMP("Not morphing a single reg call return\n");
        m_transformationDecision = BlockTransformation::SkipSingleRegCallSrc;
        m_result                 = m_store;
    }
}

//------------------------------------------------------------------------
// MorphStructCases: transforms the store as field by field copy or keeps it as a block init
//    but sets appropriate flags for the involved lclVars.
//
// Assumptions:
//    We have already checked that it is not a special case.
//
void MorphCopyBlockHelper::MorphStructCases()
{
    JITDUMP("block store to morph:\n");
    DISPTREE(m_store);

    // Check to see if we are doing a copy to/from the same local block. If so, morph it to a nop.
    // Don't do this for SSA definitions as we have no way to update downstream uses.
    if ((m_dstVarDsc != nullptr) && (m_srcVarDsc == m_dstVarDsc) && (m_dstLclOffset == m_srcLclOffset) &&
        !m_store->AsLclVarCommon()->HasSsaIdentity())
    {
        JITDUMP("Self-copy; replaced with a NOP.\n");
        m_transformationDecision = BlockTransformation::Nop;
        m_result                 = m_compiler->gtNewNothingNode();
        return;
    }

    TryPrimitiveCopy();

    if (m_transformationDecision == BlockTransformation::Undefined)
    {
        m_result                 = m_store;
        m_transformationDecision = BlockTransformation::StructBlock;
    }

    if ((m_dstVarDsc != nullptr) && m_store->OperIs(GT_STORE_LCL_FLD))
    {
        m_compiler->lvaSetVarDoNotEnregister(m_dstLclNum DEBUGARG(DoNotEnregisterReason::LocalField));
    }

    if ((m_srcVarDsc != nullptr) && m_src->OperIs(GT_LCL_FLD))
    {
        m_compiler->lvaSetVarDoNotEnregister(m_srcLclNum DEBUGARG(DoNotEnregisterReason::LocalField));
    }
}

//------------------------------------------------------------------------
// TryPrimitiveCopy: Attempt to replace a block store with a scalar store.
//
// If successful, will set "m_transformationDecision" to "OneStoreBlock".
//
void MorphCopyBlockHelper::TryPrimitiveCopy()
{
    if (!m_store->TypeIs(TYP_STRUCT))
    {
        return;
    }

    if (m_compiler->opts.OptimizationDisabled() && (m_blockSize >= genTypeSize(TYP_INT)))
    {
        return;
    }

    var_types storeType = TYP_UNDEF;

    // Can we use the LHS local directly?
    if (m_store->OperIs(GT_STORE_LCL_FLD))
    {
        if (m_blockSize == genTypeSize(m_dstVarDsc))
        {
            storeType = m_dstVarDsc->TypeGet();
        }
    }
    else if (!m_store->OperIsIndir())
    {
        return;
    }

    if (m_srcVarDsc != nullptr)
    {
        if ((storeType == TYP_UNDEF) && (m_blockSize == genTypeSize(m_srcVarDsc)))
        {
            storeType = m_srcVarDsc->TypeGet();
        }
    }
    else if (!m_src->OperIsIndir())
    {
        return;
    }

    if (storeType == TYP_UNDEF)
    {
        return;
    }

    auto doRetypeNode = [storeType](GenTree* op, LclVarDsc* varDsc, bool isUse) {
        if (op->OperIsIndir())
        {
            op->SetOper(isUse ? GT_IND : GT_STOREIND);
            op->ChangeType(storeType);
        }
        else if (varDsc->TypeGet() == storeType)
        {
            op->SetOper(isUse ? GT_LCL_VAR : GT_STORE_LCL_VAR);
            op->ChangeType(varDsc->lvNormalizeOnLoad() ? varDsc->TypeGet() : genActualType(varDsc));
            op->gtFlags &= ~GTF_VAR_USEASG;
        }
        else
        {
            if (op->OperIsScalarLocal())
            {
                op->SetOper(isUse ? GT_LCL_FLD : GT_STORE_LCL_FLD);
            }
            op->ChangeType(storeType);
        }
    };

    doRetypeNode(m_store, m_dstVarDsc, /* isUse */ false);
    doRetypeNode(m_src, m_srcVarDsc, /* isUse */ true);

    m_result                 = m_store;
    m_transformationDecision = BlockTransformation::OneStoreBlock;
}

//------------------------------------------------------------------------
// CopyFieldByField: transform the copy block to a field by field store.
//
// Notes:
//    We do it for promoted lclVars which fields can be enregistered.
//
GenTree* MorphCopyBlockHelper::CopyFieldByField()
{
    unreached();
}

//------------------------------------------------------------------------
// CanReuseAddressForDecomposedStore: Check if it is safe to reuse the
// specified address node for each decomposed store of a block copy.
//
// Arguments:
//   addrNode - The address node
//
// Return Value:
//   True if the caller can reuse the address by cloning.
//
bool MorphCopyBlockHelper::CanReuseAddressForDecomposedStore(GenTree* addrNode)
{
    if (addrNode->OperIsLocalRead())
    {
        GenTreeLclVarCommon* lcl    = addrNode->AsLclVarCommon();
        unsigned             lclNum = lcl->GetLclNum();
        LclVarDsc*           lclDsc = m_compiler->lvaGetDesc(lclNum);
        if (lclDsc->IsAddressExposed())
        {
            // Address could be pointing to itself
            return false;
        }

        // The store can also directly write to the address. For example:
        //
        //  ▌  STORE_LCL_VAR struct<Program+ListElement, 16>(P) V00 loc0
        //  ▌    long   V00.Program+ListElement:Next (offs=0x00) -> V03 tmp1
        //  ▌    int    V00.Program+ListElement:Value (offs=0x08) -> V04 tmp2
        //  └──▌  BLK       struct<Program+ListElement, 16>
        //     └──▌  LCL_VAR   long   V03 tmp1          (last use)
        //
        // If we reused the address we would produce
        //
        //  ▌  COMMA     void
        //  ├──▌  STORE_LCL_VAR long   V03 tmp1
        //  │  └──▌  IND       long
        //  │     └──▌  LCL_VAR   long   V03 tmp1          (last use)
        //  └──▌  STORE_LCL_VAR int    V04 tmp2
        //     └──▌  IND       int
        //        └──▌  ADD       byref
        //           ├──▌  LCL_VAR   long   V03 tmp1          (last use)
        //           └──▌  CNS_INT   long   8
        //
        // which is also obviously incorrect.
        //

        if (m_dstLclNum == BAD_VAR_NUM)
        {
            return true;
        }

        return lclNum != m_dstLclNum;
    }

    return addrNode->IsInvariant();
}

//------------------------------------------------------------------------
// fgMorphCopyBlock: Perform the morphing of a block copy.
//
// Arguments:
//    tree - a block copy (i.e. a store with a struct type).
//
// Return Value:
//    We can return the original block copy unmodified (least desirable, but always correct)
//    We can return a single store, when TryPrimitiveCopy transforms it (most desirable).
//    If we have performed struct promotion of the Source() or the Dest() then we will try to
//    perform a field by field store for each of the promoted struct fields.
//
// Assumptions:
//    The child nodes for tree have already been Morphed.
//
// Notes:
//    If we leave it as a block copy we will call lvaSetVarDoNotEnregister() on Source() or Dest()
//    if they cannot be enregistered.
//    When performing a field by field store we can have one of Source() or Dest treated as a blob of bytes
//    and in such cases we will call lvaSetVarDoNotEnregister() on the one treated as a blob of bytes.
//    If the Source() or Dest() is a struct that has a "CustomLayout" and "ContainsHoles" then we
//    can not use a field by field store and must leave the original block copy unmodified.
//
GenTree* Compiler::fgMorphCopyBlock(GenTree* tree)
{
    return MorphCopyBlockHelper::MorphCopyBlock(this, tree);
}

//------------------------------------------------------------------------
// fgMorphInitBlock: Morph a block initialization store tree.
//
// Arguments:
//    tree - A store tree that performs block initialization.
//
// Return Value:
//    If the destination is a promoted struct local variable then we will try to
//    perform a field by field store for each of the promoted struct fields.
//    This is not always possible (e.g. if the struct is address exposed).
//
//    Otherwise the original store tree is returned unmodified, note that the
//    nodes can still be changed.
//
// Assumptions:
//    store's children have already been morphed.
//
GenTree* Compiler::fgMorphInitBlock(GenTree* tree)
{
    return MorphInitBlockHelper::MorphInitBlock(this, tree);
}
