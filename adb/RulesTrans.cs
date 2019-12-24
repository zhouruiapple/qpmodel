﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Diagnostics;

namespace adb
{
    public class Rule
    {
        public static Rule[] ruleset_ = {
            new JoinAssociativeRule(),
            new JoinCommutativeRule(),
            new Join2NLJoin(),
            new JoinToHashJoin(),
            new Scan2Scan(),
            new Filter2Filter(),
            new Agg2HashAgg(),
            new Order2Sort(),
            new From2From(),
            new JoinCommutativeRule(),  // intentionally add a duplicated rule
        };

        public virtual bool Appliable(CGroupMember expr) => false;
        public virtual CGroupMember Apply(CGroupMember expr) =>  null;
    }

    public class ExplorationRule : Rule { }

    public class JoinCommutativeRule : ExplorationRule {
        public override bool Appliable(CGroupMember expr)
        {
            return expr.logic_ is LogicJoin lj && lj.type_ == JoinType.InnerJoin;
        }

        public override CGroupMember Apply(CGroupMember expr)
        {
            LogicJoin join = expr.logic_ as LogicJoin;
            var l = join.l_(); var r = join.r_(); var f = join.filter_;
            LogicJoin newjoin = null;
            switch (join)
            {
                case LogicSingleMarkJoin lsmj:
                    newjoin = new LogicSingleMarkJoin(r, l, f);
                    break;
                case LogicMarkSemiJoin lsm:
                    newjoin = new LogicMarkSemiJoin(r, l, f);
                    break;
                case LogicMarkAntiSemiJoin lsam:
                    newjoin = new LogicMarkAntiSemiJoin(r, l, f);
                    break;
                case LogicMarkJoin lmj:
                    newjoin = new LogicMarkJoin(r, l, f);
                    break;
                case LogicSingleJoin lsj:
                    newjoin = new LogicSingleJoin(r,l,f);
                    break;
                default:
                    newjoin = new LogicJoin(r,l,f);
                    break;
            }

            return new CGroupMember(newjoin, expr.group_);
        }
    }

    //  A(BC) => (AB)C
    //
    // 1. There are other equvalent forms and we only use above form.
    // Say (AB)C-> (AC)B which is actually can be transformed via this rule:
    //     (AB)C -> C(AB) -> (CA)B -> (AC)B
    // 2. There are also left or right association but we only use left association 
    //    since the right one can be transformed via commuative first.
    //    (AB)C -> A(BC) ; A(BC) -> (AB)C
    // 3. Join filter shall be is handled by first pull up all join filters 
    //    then push them back to the new join plan.
    //  
    //  we do not generate catersian join unless input is catersian.
    //
    public class JoinAssociativeRule : ExplorationRule
    {
        // Extract filter matching ABC's tablerefs
        // ABC=[a,b]
        //  a.i=b.i AND a.j=b.j AND a.k+b.k=c.k => a.i=b.i AND a.j=b.j
        // ABC=[a,b,c]
        //   a.i=b.i AND a.j=b.j AND a.k+b.k=c.k => a.k+b.k=c.k
        Expr exactFilter(Expr fullfilter, List<LogicNode> ABC)
        {
            Expr ret = null;
            if (fullfilter is null)
                return null;

            List<TableRef> ABCtabrefs = new List<TableRef>();
            foreach (var m in ABC)
                ABCtabrefs.AddRange(m.InclusiveTableRefs());

            var andlist = FilterHelper.FilterToAndList(fullfilter);
            foreach (var v in andlist)
            {
                var predicate = v as BinExpr;
                var predicateRefs = predicate.tableRefs_;
                if (Utils.ListAEqualsB(ABCtabrefs, predicateRefs))
                {
                    ret = FilterHelper.AddAndFilter(ret, predicate);
                }
            }

            return ret;
        }

        public override bool Appliable(CGroupMember expr)
        {
            LogicJoin a_bc = expr.logic_ as LogicJoin;

            if (a_bc != null)
            {
                var bc = (a_bc.r_() as LogicMemoRef).Deref();
                var bcfilter = bc.filter_;
                if (bc is LogicJoin) {
                    Expr abcfilter = a_bc.filter_;
                    var abfilter = exactFilter(abcfilter,
                        new List<LogicNode>(){
                            a_bc.l_(), bc.l_()});

                    // if there is no filter at all, we are fine but we don't
                    // allow the case we may generate catersian product
                    if (abfilter != null && bcfilter is null)
                        return false;
                    return true;
                }
            }
            return false;
        }

        public override CGroupMember Apply(CGroupMember expr)
        {
            LogicJoin a_bc = expr.logic_ as LogicJoin;
            LogicJoin bc = (a_bc.r_() as LogicMemoRef).Deref<LogicJoin>();
            Expr bcfilter = bc.filter_;
            var ab_c = new LogicJoin(
                new LogicJoin(a_bc.l_(), bc.l_()),
                bc.children_[1]);

            // pull up all join filters and re-push them back
            Expr allfilters = bcfilter;
            if (a_bc.filter_ != null)
                allfilters = FilterHelper.AddAndFilter(allfilters, a_bc.filter_);
            if (allfilters != null)
            {
                var andlist = FilterHelper.FilterToAndList(allfilters);
                andlist.RemoveAll(e => FilterHelper.PushJoinFilter(ab_c, e));
                if (andlist.Count > 0)
                    ab_c.filter_ = ExprHelper.AndListToExpr(andlist);
            }

            return new CGroupMember(ab_c, expr.group_);
        }
    }

}