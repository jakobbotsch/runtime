// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#include "jitpch.h"
#ifdef _MSC_VER
#pragma hdrstop
#endif

#include "treelifeupdater.h"

template <bool ForCodeGen>
TreeLifeUpdater<ForCodeGen>::TreeLifeUpdater(Compiler* m_compiler)
    : m_compiler(m_compiler)
#ifdef DEBUG
    , epoch(m_compiler->GetCurLVEpoch())
    , oldLife(VarSetOps::MakeEmpty(m_compiler))
    , oldStackPtrsLife(VarSetOps::MakeEmpty(m_compiler))
#endif // DEBUG
{
}

//------------------------------------------------------------------------
// UpdateLifeVar: Update live sets for a given tree.
//
// Arguments:
//    tree       - the tree which affects liveness
//    lclVarTree - the local tree
//
// Notes:
//    Most commonly "tree" and "lclVarTree" will be the same, however,
//    that will not be true for indirect defs ("STOREIND(LCL_ADDR, ...)")
//    and uses ("OBJ(LCL_ADDR)")
//
template <bool ForCodeGen>
void TreeLifeUpdater<ForCodeGen>::UpdateLifeVar(GenTree* tree, GenTreeLclVarCommon* lclVarTree)
{
    assert(lclVarTree->OperIsNonPhiLocal() || lclVarTree->OperIs(GT_LCL_ADDR));

    unsigned int lclNum = lclVarTree->GetLclNum();
    LclVarDsc*   varDsc = m_compiler->lvaGetDesc(lclNum);

    m_compiler->compCurLifeTree = tree;

    if (!varDsc->lvTracked)
    {
        return;
    }

    StoreCurrentLifeForDump();

    const bool isBorn = ((lclVarTree->gtFlags & GTF_VAR_DEF) != 0) && ((lclVarTree->gtFlags & GTF_VAR_USEASG) == 0);

    if (varDsc->lvTracked)
    {
        assert(!lclVarTree->IsMultiRegLclVar());

        const bool isDying = (lclVarTree->gtFlags & GTF_VAR_DEATH) != 0;

        if (isBorn || isDying)
        {
            const bool previouslyLive =
                ForCodeGen && VarSetOps::IsMember(m_compiler, m_compiler->compCurLife, varDsc->lvVarIndex);
            UpdateLifeBit(m_compiler->compCurLife, varDsc, isBorn, isDying);

            if (ForCodeGen)
            {
                if (isBorn && varDsc->lvIsRegCandidate() && tree->gtHasReg(m_compiler))
                {
                    m_compiler->codeGen->genUpdateVarReg(varDsc, tree);
                }

                const bool isInReg    = varDsc->lvIsInReg() && (tree->GetRegNum() != REG_NA);
                const bool isInMemory = !isInReg || varDsc->IsAlwaysAliveInMemory();
                if (isInReg)
                {
                    m_compiler->codeGen->genUpdateRegLife(varDsc, isBorn, isDying DEBUGARG(tree));
                }

                if (isInMemory &&
                    VarSetOps::IsMember(m_compiler, m_compiler->codeGen->gcInfo.gcTrkStkPtrLcls, varDsc->lvVarIndex))
                {
                    UpdateLifeBit(m_compiler->codeGen->gcInfo.gcVarPtrSetCur, varDsc, isBorn, isDying);
                }

                if (isDying == previouslyLive)
                {
                    m_compiler->codeGen->getVariableLiveKeeper()->siStartOrCloseVariableLiveRange(varDsc, lclNum,
                                                                                                  !isDying, isDying);
                }
            }
        }

        if (ForCodeGen && ((lclVarTree->gtFlags & GTF_SPILL) != 0))
        {
            m_compiler->codeGen->genSpillVar(tree);

            if (VarSetOps::IsMember(m_compiler, m_compiler->codeGen->gcInfo.gcTrkStkPtrLcls, varDsc->lvVarIndex))
            {
                if (!VarSetOps::IsMember(m_compiler, m_compiler->codeGen->gcInfo.gcVarPtrSetCur, varDsc->lvVarIndex))
                {
                    VarSetOps::AddElemD(m_compiler, m_compiler->codeGen->gcInfo.gcVarPtrSetCur, varDsc->lvVarIndex);
                    JITDUMP("\t\t\t\t\t\t\tVar V%02u becoming live\n", lclNum);
                }
            }
        }
    }


    DumpLifeDelta(tree);
}

//------------------------------------------------------------------------
// UpdateLife: Determine whether the tree affects liveness, and update liveness sets accordingly.
//
// Arguments:
//    tree - the tree which effect on liveness is processed.
//
template <bool ForCodeGen>
template <bool GeneralLclAddrHandling>
void TreeLifeUpdater<ForCodeGen>::UpdateLife(GenTree* tree)
{
    assert(m_compiler->GetCurLVEpoch() == epoch);
    // TODO-Cleanup: We shouldn't really be calling this more than once
    if (tree == m_compiler->compCurLifeTree)
    {
        return;
    }

    // Note that after lowering, we can see indirect uses and definitions of tracked variables.
    if (tree->OperIsNonPhiLocal())
    {
        UpdateLifeVar(tree, tree->AsLclVarCommon());
    }
    else if (!GeneralLclAddrHandling && tree->OperIsIndir() && tree->AsIndir()->Addr()->OperIs(GT_LCL_ADDR))
    {
        UpdateLifeVar(tree, tree->AsIndir()->Addr()->AsLclVarCommon());
    }
    else if (tree->IsCall())
    {
        auto visitDef = [=](GenTreeLclVarCommon* lcl) {
            UpdateLifeVar(tree, lcl);
            return GenTree::VisitResult::Continue;
        };
        tree->VisitLocalDefNodes(m_compiler, visitDef);
    }
    else if (GeneralLclAddrHandling && tree->OperIs(GT_LCL_ADDR))
    {
        UpdateLifeVar(tree, tree->AsLclVarCommon());
    }
}

//------------------------------------------------------------------------
// UpdateLifeBit: Update a liveness set for a specific local depending on whether it is being born or dying.
//
// Arguments:
//    set - The life set
//    dsc - The local's description
//    isBorn - Whether the local is being born now
//    isDying - Whether the local is dying now
//
template <bool ForCodeGen>
void TreeLifeUpdater<ForCodeGen>::UpdateLifeBit(VARSET_TP& set, LclVarDsc* dsc, bool isBorn, bool isDying)
{
    if (isDying)
    {
        VarSetOps::RemoveElemD(m_compiler, set, dsc->lvVarIndex);
    }
    else if (isBorn)
    {
        VarSetOps::AddElemD(m_compiler, set, dsc->lvVarIndex);
    }
}

//------------------------------------------------------------------------
// StoreCurrentLifeForDump: Store current liveness information so that deltas
// can be dumped after potential updates.
//
template <bool ForCodeGen>
void TreeLifeUpdater<ForCodeGen>::StoreCurrentLifeForDump()
{
#ifdef DEBUG
    if (m_compiler->verbose)
    {
        VarSetOps::Assign(m_compiler, oldLife, m_compiler->compCurLife);

        if (ForCodeGen)
        {
            VarSetOps::Assign(m_compiler, oldStackPtrsLife, m_compiler->codeGen->gcInfo.gcVarPtrSetCur);
        }
    }
#endif
}

//------------------------------------------------------------------------
// DumpLifeDelta: Dump the delta of liveness changes that happened since
// StoreCurrentLifeForDump was called.
//
template <bool ForCodeGen>
void TreeLifeUpdater<ForCodeGen>::DumpLifeDelta(GenTree* tree)
{
#ifdef DEBUG
    if (m_compiler->verbose && !VarSetOps::Equal(m_compiler, oldLife, m_compiler->compCurLife))
    {
        printf("\t\t\t\t\t\t\tLive vars after [%06u]: ", Compiler::dspTreeID(tree));
        dumpConvertedVarSet(m_compiler, oldLife);

        // deadSet = oldLife - compCurLife
        VARSET_TP deadSet(VarSetOps::Diff(m_compiler, oldLife, m_compiler->compCurLife));

        // bornSet = compCurLife - oldLife
        VARSET_TP bornSet(VarSetOps::Diff(m_compiler, m_compiler->compCurLife, oldLife));

        if (!VarSetOps::IsEmpty(m_compiler, deadSet))
        {
            printf(" -");
            dumpConvertedVarSet(m_compiler, deadSet);
        }

        if (!VarSetOps::IsEmpty(m_compiler, bornSet))
        {
            printf(" +");
            dumpConvertedVarSet(m_compiler, bornSet);
        }

        printf(" => ");
        dumpConvertedVarSet(m_compiler, m_compiler->compCurLife);
        printf("\n");
    }

    if (ForCodeGen && m_compiler->verbose &&
        !VarSetOps::Equal(m_compiler, oldStackPtrsLife, m_compiler->codeGen->gcInfo.gcVarPtrSetCur))
    {
        printf("\t\t\t\t\t\t\tGC vars after [%06u]: ", Compiler::dspTreeID(tree));
        dumpConvertedVarSet(m_compiler, oldStackPtrsLife);
        printf(" => ");
        dumpConvertedVarSet(m_compiler, m_compiler->codeGen->gcInfo.gcVarPtrSetCur);
        printf("\n");
    }
#endif // DEBUG
}

template class TreeLifeUpdater<true>;
template class TreeLifeUpdater<false>;
template void TreeLifeUpdater<false>::UpdateLife<false>(GenTree*);
template void TreeLifeUpdater<false>::UpdateLife<true>(GenTree*);
template void TreeLifeUpdater<true>::UpdateLife<false>(GenTree*);
