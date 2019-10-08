﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Diagnostics;

namespace adb
{
    public abstract class SQLStatement
    {
        internal bool explain_ = false;

        // bounded context
        internal BindContext bindContext_;

        // plan
        public LogicNode logicPlan_;
        public PhysicNode physicPlan_;

        // debug support
        internal string text_;

        public virtual SQLStatement Bind(BindContext parent) { return this; }
        public virtual LogicNode Optimize() => logicPlan_;
        public virtual LogicNode CreatePlan() => logicPlan_;
    }

    /*
    SQL is implemented as if a query was executed in the following order:

        FROM clause
        WHERE clause
        GROUP BY clause
        HAVING clause
        SELECT clause
        ORDER BY clause
    */
    public class SelectStmt : SQLStatement {
        internal List<TableRef> from_;
        internal Expr where_;
        internal List<Expr> groupby_;
        internal Expr having_;
        internal List<Expr> selection_;

        public List<CTExpr> ctes_;
        public List<SelectStmt> setqs_ = new List<SelectStmt>();
        public List<OrderTerm> orders_;

        internal bool hasOuterRef_ = false;
        bool hasParent_ = false;

        public SelectStmt(
            // these fields are ok with select_core
            List<Expr> selection, List<TableRef> from, Expr where, List<Expr> groupby, Expr having,
            // these fields are only avaliable for top query
            List<CTExpr> ctes, List<SelectStmt> cores, List<OrderTerm> orders, 
            string text)
        {
            selection_ = selection;
            from_ = from;
            where_ = where;
            groupby_ = groupby;
            having_ = having;

            ctes_ = ctes;
            setqs_ = cores;
            orders_ = orders;

            text_ = text;
        }

        void bindFrom(BindContext context)
        {
            from_.ForEach(x => {
                switch (x)
                {
                    case BaseTableRef bref:
                        if (Catalog.systable_.Table(bref.relname_) != null)
                            context.AddTable(bref);
                        else
                            throw new Exception($@"base table {bref.alias_} not exists");
                        break;
                    case FromQueryRef sref:
                        sref.query_.Bind(context);

                        // the subquery itself in from clause can be seen as a new table, so register it here
                        context.AddTable(sref);
                        break;
                }
            });
        }
        void bindSelectionList(BindContext context)
        {
            List<SelStar> selstars = new List<SelStar>();
            selection_.ForEach(x => {
                if (x is SelStar xs)
                    selstars.Add(xs);
				else
					x.Bind(context);
            });

            // expand * into actual columns
            selstars.ForEach(x => {
                selection_.Remove(x); selection_.AddRange(x.Expand(context)); });
        }
        void bindWhere(BindContext context) => where_?.Bind(context);
        void bindGroupBy(BindContext context)=> groupby_?.ForEach(x => x.Bind(context));
        void bindHaving(BindContext context) => having_?.Bind(context);
        public override SQLStatement Bind(BindContext parent)
        {
            BindContext context = new BindContext(this, parent);
            hasParent_ = (parent != null);

            Debug.Assert(logicPlan_ == null);

            // from binding shall be the first since it may create new alias
            bindFrom(context);
            bindSelectionList(context);
            bindWhere(context);
            bindGroupBy(context);
            bindHaving(context);

            bindContext_ = context;
            return this;
        }

        LogicNode transformOneFrom(TableRef tab)
        {
            LogicNode from;
            switch (tab)
            {
                case BaseTableRef bref:
                    from = new LogicGet(bref);
                    break;
                case FromQueryRef sref:
                    from = new LogicFromQuery(sref,
                                    sref.query_.CreatePlan());
                    break;
                default:
                    throw new Exception();
            }

            return from;
        }

        // from clause -
        //  pair each from item with cross join, their join conditions will be handled
        //  with where clauss processing.
        //
        LogicNode transformFromClause()
        {
            LogicNode root;

            if (from_.Count >= 2)
            {
                var join = new LogicCrossJoin(null, null);
                var children = join.children_;
                from_.ForEach(x =>
                {
                    LogicNode from = transformOneFrom(x);
                    if (children[0] is null)
                        children[0] = from;
                    else
                        children[1] = (children[1] is null) ? from :
                                        new LogicCrossJoin(from, children[1]);
                });
                root = join;
            }
            else if (from_.Count == 1)
                root = transformOneFrom(from_[0]);
            else
                root = new LogicResult(selection_);

            return root;
        }

        void createSubQueryExprPlan(Expr expr)
        {
            if (!expr.HasSubQuery())
                return;
            if (expr.HasSubQuery())
            {
                expr.VisitEachExpr(x =>
                {
                    if (x is SubqueryExpr sx)
                    {
                        sx.query_.CreatePlan();
                    }
                    return false;
                });
            }
        }

        public override LogicNode CreatePlan()
        {
            LogicNode root = transformFromClause();

            // transform where clause
            if (where_ != null)
            {
                createSubQueryExprPlan(where_);
                root = new LogicFilter(root, where_);
            }

            // group by
            if (groupby_ != null)
                root = new LogicAgg(root, groupby_, having_);

            // selection list
            selection_.ForEach(x => createSubQueryExprPlan(x));

            // resolve the output
            root.ResolveChildrenColumns(selection_, hasParent_);

            logicPlan_ = root;
            return root;
        }

        void breakEachAndExprs(LogicAndExpr andexpr, List<Expr> andlist)
        {
            for (int i = 0; i < 2; i++)
            {
                Expr e = i == 0 ? andexpr.l_ : andexpr.r_;
                if (e is LogicAndExpr ea)
                    breakEachAndExprs(ea, andlist);
                else
                    andlist.Add(e);
            }
        }

        bool pushdownATableFilter(LogicNode plan, Expr filter)
        {
            return plan.VisitEachNode(n =>
            {
                if (n is LogicGet nodeGet &&
                    filter.EqualTableRefs(bindContext_, nodeGet.tabref_))
                    return nodeGet.AddFilter(filter);
                return false;
            });
        }

        public override LogicNode Optimize()
        {
            LogicNode plan = logicPlan_;
            var filter = plan as LogicFilter;
            if (filter is null || filter.filter_ is null)
                goto Convert;

            // filter push down
            var topfilter = filter.filter_;
            List<Expr> andlist = new List<Expr>();
            if (topfilter is LogicAndExpr andexpr)
                breakEachAndExprs(andexpr, andlist);
            else
                andlist.Add(topfilter);
            andlist.RemoveAll(e => pushdownATableFilter(plan, e));

            if (andlist.Count == 0)
            {
                // top filter node is not needed
                plan = plan.children_[0];
                plan.ClearOutput();
                plan.ResolveChildrenColumns(selection_, hasParent_);
                logicPlan_ = plan;
            }
            else 
                filter.filter_ = ExprHelper.AndListToExpr(andlist);

Convert:        
            // convert to physical plan
            physicPlan_ = logicPlan_.DirectToPhysical();
            selection_.ForEach(x => ExprHelper.SubqueryDirectToPhysic(x));
            return plan;
        }
    }
}
