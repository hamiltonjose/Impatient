﻿using Impatient.Query.Expressions;
using Impatient.Query.ExpressionVisitors.Utility;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using static Impatient.ImpatientExtensions;

namespace Impatient.Query.ExpressionVisitors
{
    public class QueryComposingExpressionVisitor : ExpressionVisitor
    {
        private static readonly KeyPlaceholderGroupingInjectingExpressionVisitor keyPlaceholderGroupingInjector
            = new KeyPlaceholderGroupingInjectingExpressionVisitor();

        private static readonly GroupExpandingExpressionVisitor groupExpander
            = new GroupExpandingExpressionVisitor();

        private IEnumerable<ExpressionVisitor> PostExpansionVisitors
        {
            get
            {
                yield return this;

                yield return new GroupingAggregationRewritingExpressionVisitor(
                    expressionVisitorProvider.TranslatabilityAnalyzingExpressionVisitor);

                foreach (var rewritingVisitor in expressionVisitorProvider.RewritingExpressionVisitors)
                {
                    yield return rewritingVisitor;
                }
            }
        }

        private readonly IImpatientExpressionVisitorProvider expressionVisitorProvider;

        private bool topLevel = true;

        public QueryComposingExpressionVisitor(IImpatientExpressionVisitorProvider expressionVisitorProvider)
        {
            this.expressionVisitorProvider = expressionVisitorProvider 
                ?? throw new ArgumentNullException(nameof(expressionVisitorProvider));
        }

        public override Expression Visit(Expression node)
        {
            if (topLevel)
            {
                topLevel = false;

                var visited = base.Visit(node);

                topLevel = true;

                return groupExpander.Visit(visited);
            }

            return base.Visit(node);
        }

        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            if (node.Method.DeclaringType == typeof(Queryable)
                || node.Method.DeclaringType == typeof(Enumerable))
            {
                var visitedArguments = new Expression[node.Arguments.Count];

                var outerSource = visitedArguments[0] = ProcessQuerySource(Visit(node.Arguments[0]));

                if (outerSource is EnumerableRelationalQueryExpression outerQuery)
                {
                    var isQueryable = node.Method.DeclaringType == typeof(Queryable);

                    switch (node.Method.Name)
                    {
                        // Pass-through operations

                        case nameof(Queryable.AsQueryable):
                        case nameof(Enumerable.ToArray):
                        case nameof(Enumerable.ToList):
                        {
                            return outerQuery.WithTransformationMethod(node.Method);
                        }

                        // Projection operations

                        case nameof(Queryable.Select):
                        {
                            var selectorLambda = node.Arguments[1].UnwrapLambda();

                            if (selectorLambda.Parameters.Count != 1)
                            {
                                // TODO: Add support for the index parameter
                                goto ReturnEnumerableCall;
                            }

                            var outerSelectExpression = outerQuery.SelectExpression;
                            var outerProjection = outerSelectExpression.Projection.Flatten().Body;

                            if (outerSelectExpression.IsDistinct
                                || outerSelectExpression.Limit != null
                                || outerSelectExpression.Offset != null)
                            {
                                if (!IsTranslatable(outerProjection))
                                {
                                    goto ReturnEnumerableCall;
                                }

                                Pushdown(
                                    selectorLambda.Parameters[0].Name,
                                    ref outerSelectExpression,
                                    ref outerProjection,
                                    out _);
                            }

                            var selectorBody
                                = selectorLambda
                                    .ExpandParameters(outerProjection)
                                    .VisitWith(PostExpansionVisitors);

                            if (IsTranslatable(selectorBody))
                            {
                                return outerQuery
                                    .UpdateSelectExpression(outerSelectExpression
                                        .UpdateProjection(new ServerProjectionExpression(
                                            selectorBody)));
                            }

                            return outerQuery
                                .UpdateSelectExpression(outerSelectExpression
                                    .UpdateProjection(outerSelectExpression.Projection
                                        .Merge(Expression.Lambda(
                                            selectorLambda.Body.VisitWith(PostExpansionVisitors),
                                            selectorLambda.Parameters[0]))));
                        }

                        case nameof(Queryable.SelectMany):
                        {
                            if (node.Arguments.Count != 3)
                            {
                                // TODO: Support overloads that only use selector
                                goto ReturnEnumerableCall;
                            }

                            var collectionSelector = node.Arguments[1].UnwrapLambda();

                            if (collectionSelector.Parameters.Count != 1)
                            {
                                // TODO: Support overloads that use the index parameter
                                goto ReturnEnumerableCall;
                            }

                            var resultSelector = node.Arguments[2].UnwrapLambda();

                            var outerSelectExpression = outerQuery.SelectExpression;
                            var outerTable = outerSelectExpression.Table;
                            var outerProjection = outerSelectExpression.Projection.Flatten().Body;
                            var outerRequiresPushdown = outerSelectExpression.RequiresPushdownForLeftSideOfJoin();

                            if (outerRequiresPushdown)
                            {
                                if (!(IsTranslatable(outerProjection)))
                                {
                                    goto ReturnEnumerableCall;
                                }

                                Pushdown(
                                    resultSelector.Parameters[0].Name,
                                    ref outerSelectExpression,
                                    ref outerProjection,
                                    out outerTable);
                            }

                            var innerSource = collectionSelector.ExpandParameters(outerProjection);
                            var handleAsCorrelated = innerSource != collectionSelector.Body;
                            var handleAsJoin = false;
                            var joinPredicate = default(Expression);
                            var defaultIfEmpty = false;

                            if (innerSource is MethodCallExpression innerSourceMethodCall
                                && (innerSourceMethodCall.Method.DeclaringType == typeof(Queryable)
                                    || innerSourceMethodCall.Method.DeclaringType == typeof(Enumerable))
                                && innerSourceMethodCall.Method.Name == nameof(Queryable.DefaultIfEmpty)
                                && innerSourceMethodCall.Arguments.Count == 1)
                            {
                                defaultIfEmpty = true;
                                innerSource = innerSourceMethodCall.Arguments[0];
                            }

                            if (innerSource is GroupedRelationalQueryExpression groupedRelationalQueryExpression)
                            {
                                handleAsCorrelated = false;
                                handleAsJoin = true;

                                joinPredicate
                                    = Expression.Equal(
                                        groupedRelationalQueryExpression.OuterKeySelector,
                                        groupedRelationalQueryExpression.InnerKeySelector);

                                innerSource
                                    = new EnumerableRelationalQueryExpression(
                                        groupedRelationalQueryExpression.SelectExpression);
                            }

                            innerSource = ProcessQuerySource(innerSource).VisitWith(PostExpansionVisitors);

                            if (!(innerSource is EnumerableRelationalQueryExpression innerQuery))
                            {
                                goto ReturnEnumerableCall;
                            }

                            var innerSelectExpression
                                = new TableUniquifyingExpressionVisitor()
                                    .VisitAndConvert(innerQuery.SelectExpression, nameof(VisitMethodCall));

                            var innerTable = innerSelectExpression.Table as AliasedTableExpression;

                            var innerProjection = innerSelectExpression.Projection.Flatten().Body;

                            var innerRequiresPushdown
                                = innerSelectExpression.RequiresPushdownForRightSideOfJoin()
                                    || handleAsCorrelated
                                    || defaultIfEmpty;

                            if (innerRequiresPushdown)
                            {
                                if (!(IsTranslatable(innerProjection)))
                                {
                                    goto ReturnEnumerableCall;
                                }

                                if (defaultIfEmpty)
                                {
                                    innerSelectExpression
                                        = innerSelectExpression.UpdateProjection(
                                            new ServerProjectionExpression(
                                                new DefaultIfEmptyFlagExpression(
                                                    innerProjection)));
                                }

                                innerTable
                                    = new SubqueryTableExpression(
                                        keyPlaceholderGroupingInjector.VisitAndConvert(innerSelectExpression, nameof(VisitMethodCall)),
                                        resultSelector.Parameters[1].Name);

                                innerProjection
                                    = new ProjectionReferenceRewritingExpressionVisitor(innerTable)
                                        .Visit(innerProjection)
                                        .VisitWith(PostExpansionVisitors);

                                if (defaultIfEmpty)
                                {
                                    innerProjection
                                        = new DefaultIfEmptyTestExpression(
                                            innerProjection,
                                            innerTable);
                                }

                                innerSelectExpression
                                    = new SelectExpression(
                                        new ServerProjectionExpression(innerProjection),
                                        innerTable);
                            }

                            if (handleAsJoin)
                            {
                                var oldTables = innerQuery.SelectExpression.Table.Flatten();
                                var newTables = innerSelectExpression.Table.Flatten();

                                var replacingVisitor
                                    = new ExpressionReplacingExpressionVisitor(
                                        oldTables.Zip(newTables, ValueTuple.Create<Expression, Expression>)
                                            .ToDictionary(t => t.Item1, t => t.Item2));

                                joinPredicate = replacingVisitor.Visit(joinPredicate);
                            }

                            var selector
                                = resultSelector
                                    .ExpandParameters(outerProjection, innerProjection)
                                    .VisitWith(PostExpansionVisitors);

                            var projection
                                = IsTranslatable(selector)
                                    ? new ServerProjectionExpression(selector)
                                    : new CompositeProjectionExpression(
                                        outerSelectExpression.Projection,
                                        innerSelectExpression.Projection,
                                        resultSelector) as ProjectionExpression;

                            var table
                                = handleAsJoin
                                    ? defaultIfEmpty
                                        ? new LeftJoinExpression(outerTable, innerTable, joinPredicate, selector.Type)
                                        : new InnerJoinExpression(outerTable, innerTable, joinPredicate, selector.Type)
                                            as TableExpression
                                    : handleAsCorrelated
                                        ? defaultIfEmpty
                                            ? new OuterApplyExpression(outerTable, innerTable, selector.Type)
                                            : new CrossApplyExpression(outerTable, innerTable, selector.Type)
                                                as TableExpression
                                        : new CrossJoinExpression(outerTable, innerTable, selector.Type);

                            return outerQuery
                                .UpdateSelectExpression(outerSelectExpression
                                    .UpdateProjection(projection)
                                    .UpdateTable(table));
                        }

                        // Join operations

                        case nameof(Queryable.Join):
                        {
                            if (node.Arguments.Count != 5)
                            {
                                // TODO: Investigate possibility of supporting IEqualityComparer overloads
                                goto ReturnEnumerableCall;
                            }

                            var innerSource = visitedArguments[1] = ProcessQuerySource(Visit(node.Arguments[1]));

                            if (!(innerSource is EnumerableRelationalQueryExpression innerQuery))
                            {
                                goto ReturnEnumerableCall;
                            }

                            // TODO: Inspect whether the outer query needs to be pushed down
                            var outerSelectExpression = outerQuery.SelectExpression;
                            var outerTable = outerSelectExpression.Table;
                            var outerProjection = outerSelectExpression.Projection.Flatten().Body;
                            var outerKeyLambda = node.Arguments[2].UnwrapLambda();

                            if (outerSelectExpression.RequiresPushdownForLeftSideOfJoin())
                            {
                                if (!IsTranslatable(outerProjection))
                                {
                                    goto ReturnEnumerableCall;
                                }

                                // TODO: What about ordering?
                                Pushdown(
                                    outerKeyLambda.Parameters[0].Name,
                                    ref outerSelectExpression,
                                    ref outerProjection,
                                    out outerTable);
                            }

                            var outerKeySelector
                                = outerKeyLambda
                                    .ExpandParameters(outerProjection)
                                    .VisitWith(PostExpansionVisitors);

                            if (!IsTranslatable(outerKeySelector))
                            {
                                goto ReturnEnumerableCall;
                            }

                            var innerSelectExpression = innerQuery.SelectExpression;
                            var innerTable = innerSelectExpression.Table;
                            var innerProjection = innerSelectExpression.Projection.Flatten().Body;
                            var innerKeyLambda = node.Arguments[3].UnwrapLambda();

                            if (innerSelectExpression.RequiresPushdownForRightSideOfJoin())
                            {
                                if (!IsTranslatable(innerProjection))
                                {
                                    goto ReturnEnumerableCall;
                                }

                                Pushdown(
                                    innerKeyLambda.Parameters[0].Name,
                                    ref innerSelectExpression,
                                    ref innerProjection,
                                    out innerTable);
                            }

                            var innerKeySelector
                                = innerKeyLambda
                                    .ExpandParameters(innerProjection)
                                    .VisitWith(PostExpansionVisitors);

                            if (!IsTranslatable(innerKeySelector))
                            {
                                goto ReturnEnumerableCall;
                            }

                            var resultLambda
                                = node.Arguments[4]
                                    .UnwrapLambda();

                            var resultSelector
                                = resultLambda
                                    .ExpandParameters(outerProjection, innerProjection)
                                    .VisitWith(PostExpansionVisitors);

                            var projection
                                = IsTranslatable(resultSelector)
                                    ? new ServerProjectionExpression(resultSelector)
                                    : new CompositeProjectionExpression(
                                        outerSelectExpression.Projection,
                                        new ServerProjectionExpression(innerProjection),
                                        resultLambda) as ProjectionExpression;

                            var table
                                = new InnerJoinExpression(
                                    outerTable,
                                    innerTable as AliasedTableExpression,
                                    Expression.Equal(outerKeySelector, innerKeySelector),
                                    resultSelector.Type);

                            return outerQuery
                                .UpdateSelectExpression(outerSelectExpression
                                    .UpdateProjection(projection)
                                    .UpdateTable(table));
                        }

                        case nameof(Queryable.GroupJoin):
                        {
                            var innerSource = visitedArguments[1] = ProcessQuerySource(Visit(node.Arguments[1]));

                            if (!(innerSource is EnumerableRelationalQueryExpression innerQuery))
                            {
                                goto ReturnEnumerableCall;
                            }

                            var outerSelectExpression = outerQuery.SelectExpression;
                            var outerProjection = outerSelectExpression.Projection.Flatten().Body;
                            var outerKeyLambda = node.Arguments[2].UnwrapLambda();

                            var outerKeySelector
                                = outerKeyLambda
                                    .ExpandParameters(outerProjection)
                                    .VisitWith(PostExpansionVisitors);

                            if (!IsTranslatable(outerKeySelector))
                            {
                                goto ReturnEnumerableCall;
                            }

                            var innerSelectExpression = innerQuery.SelectExpression;
                            var innerProjection = innerSelectExpression.Projection.Flatten().Body;
                            var innerKeyLambda = node.Arguments[3].UnwrapLambda();

                            var innerKeySelector
                                = innerKeyLambda
                                    .ExpandParameters(innerProjection)
                                    .VisitWith(PostExpansionVisitors);

                            if (!IsTranslatable(innerKeySelector))
                            {
                                goto ReturnEnumerableCall;
                            }

                            var resultLambda
                                = node.Arguments[4]
                                    .UnwrapLambda();

                            innerProjection
                                = new GroupedRelationalQueryExpression(
                                    innerSelectExpression,
                                    outerKeySelector,
                                    innerKeySelector,
                                    resultLambda.Parameters[1].Type);

                            var resultSelector
                                = resultLambda
                                    .ExpandParameters(
                                        outerProjection,
                                        innerProjection)
                                    .VisitWith(PostExpansionVisitors);

                            if (IsTranslatable(resultSelector))
                            {
                                return outerQuery
                                    .UpdateSelectExpression(outerSelectExpression
                                        .UpdateProjection(new ServerProjectionExpression(
                                            resultSelector)));
                            }

                            innerProjection
                                = new GroupedRelationalQueryExpression(
                                    innerSelectExpression,
                                    outerKeyLambda.Body.Replace(
                                        outerKeyLambda.Parameters[0],
                                        resultLambda.Parameters[0]),
                                    innerKeySelector,
                                    resultLambda.Parameters[1].Type);

                            resultLambda
                                = Expression.Lambda(
                                    resultLambda.Body
                                        .Replace(resultLambda.Parameters[1], innerProjection)
                                        .VisitWith(PostExpansionVisitors),
                                    resultLambda.Parameters[0]);

                            return outerQuery
                                .UpdateSelectExpression(outerSelectExpression
                                    .UpdateProjection(
                                        outerSelectExpression.Projection.Merge(resultLambda)));
                        }

                        // Filtering operations

                        case nameof(Queryable.Where):
                        {
                            var predicateLambda = node.Arguments[1].UnwrapLambda();

                            if (predicateLambda.Parameters.Count != 1)
                            {
                                // TODO: add support for index parameter overload
                                goto ReturnEnumerableCall;
                            }

                            var outerSelectExpression = outerQuery.SelectExpression;
                            var outerProjection = outerSelectExpression.Projection.Flatten().Body;

                            if (outerSelectExpression.IsDistinct
                                || outerSelectExpression.Limit != null
                                || outerSelectExpression.Offset != null)
                            {
                                if (!IsTranslatable(outerProjection))
                                {
                                    goto ReturnEnumerableCall;
                                }

                                Pushdown(
                                    predicateLambda.Parameters[0].Name,
                                    ref outerSelectExpression,
                                    ref outerProjection,
                                    out _);
                            }

                            var predicateBody
                                = predicateLambda
                                    .ExpandParameters(outerProjection)
                                    .VisitWith(PostExpansionVisitors);

                            if (IsTranslatable(predicateBody))
                            {
                                return outerQuery
                                    .UpdateSelectExpression(outerSelectExpression
                                        .AddToPredicate(predicateBody));
                            }

                            if (predicateBody is BinaryExpression resolvedBinaryPredicate
                                && resolvedBinaryPredicate.NodeType == ExpressionType.AndAlso
                                && predicateLambda.Body is BinaryExpression unresolvedBinaryPredicate)
                            {
                                if (IsTranslatable(resolvedBinaryPredicate.Left))
                                {
                                    visitedArguments[0]
                                        = outerQuery
                                            .UpdateSelectExpression(outerSelectExpression
                                                .AddToPredicate(resolvedBinaryPredicate.Left));

                                    visitedArguments[1]
                                        = Expression.Lambda(
                                            unresolvedBinaryPredicate.Right,
                                            predicateLambda.Parameters);

                                    goto ReturnEnumerableCall;
                                }

                                if (IsTranslatable(resolvedBinaryPredicate.Right))
                                {
                                    visitedArguments[0]
                                        = outerQuery
                                            .UpdateSelectExpression(outerSelectExpression
                                                .AddToPredicate(resolvedBinaryPredicate.Right));

                                    visitedArguments[1]
                                        = Expression.Lambda(
                                            unresolvedBinaryPredicate.Left,
                                            predicateLambda.Parameters);

                                    goto ReturnEnumerableCall;
                                }
                            }

                            goto ReturnEnumerableCall;
                        }

                        case nameof(Queryable.OfType):
                        {
                            var inType = outerQuery.SelectExpression.Projection.Type;
                            var outType = node.Method.GetGenericArguments()[0];

                            if (inType == outType)
                            {
                                return outerQuery;
                            }

                            // TODO: Handle downcasting within type hierarchies?
                            // TODO: Inspect whether the outer query needs to be pushed down
                            goto ReturnEnumerableCall;
                        }

                        // Grouping operations

                        case nameof(Queryable.GroupBy):
                        {
                            if (typeof(IEqualityComparer<>) == node.Method.GetParameters().Last().ParameterType.GetGenericTypeDefinition())
                            {
                                // TODO: Investigate possibility of supporting IEqualityComparer overloads
                                goto ReturnEnumerableCall;
                            }

                            // TODO: Inspect whether the outer query needs to be pushed down

                            var outerSelectExpression = outerQuery.SelectExpression;
                            var outerProjection = outerSelectExpression.Projection.Flatten().Body;
                            var nodeMethodGenericMethodDefinition = node.Method.GetGenericMethodDefinition();

                            // Key Selector

                            var keySelectorLambda = node.Arguments[1].UnwrapLambda();

                            var keySelector
                                = keySelectorLambda
                                    .ExpandParameters(outerProjection)
                                    .VisitWith(PostExpansionVisitors);

                            if (!IsTranslatable(keySelector))
                            {
                                goto ReturnEnumerableCall;
                            }

                            // Element Selector

                            var elementSelector = outerProjection;

                            if (nodeMethodGenericMethodDefinition == groupByKeyElement
                                || nodeMethodGenericMethodDefinition == groupByKeyElementResult)
                            {
                                elementSelector
                                    = node.Arguments[2]
                                        .UnwrapLambda()
                                        .ExpandParameters(elementSelector)
                                        .VisitWith(PostExpansionVisitors);
                            }

                            if (!IsTranslatable(elementSelector))
                            {
                                goto ReturnEnumerableCall;
                            }

                            // Result Selector

                            if (nodeMethodGenericMethodDefinition == groupByKeyResult
                                || nodeMethodGenericMethodDefinition == groupByKeyElementResult)
                            {
                                var resultLambda = node.Arguments[node.Arguments.Count - 1].UnwrapLambda();

                                var resultSelector
                                    = resultLambda
                                        .ExpandParameters(
                                            keySelector,
                                            new GroupByResultExpression(
                                                outerSelectExpression,
                                                keySelector,
                                                keySelector,
                                                elementSelector,
                                                false))
                                        .VisitWith(PostExpansionVisitors);

                                if (IsTranslatable(resultSelector))
                                {
                                    return outerQuery
                                        .UpdateSelectExpression(outerSelectExpression
                                            .UpdateProjection(new ServerProjectionExpression(resultSelector))
                                            .UpdateGrouping(keySelector));
                                }

                                return outerQuery
                                    .UpdateSelectExpression(outerSelectExpression
                                        .UpdateProjection(
                                            new ClientProjectionExpression(
                                                new ServerProjectionExpression(keySelector),
                                                Expression.Lambda(
                                                    resultLambda.Body.Replace(
                                                        resultLambda.Parameters[1],
                                                        new GroupByResultExpression(
                                                            outerSelectExpression,
                                                            keySelectorLambda.Body.Replace(
                                                                keySelectorLambda.Parameters[0],
                                                                resultLambda.Parameters[0]),
                                                            keySelector,
                                                            elementSelector,
                                                            false)),
                                                    resultLambda.Parameters[0])))
                                        .UpdateGrouping(keySelector));
                            }
                            else
                            {
                                var resultSelector
                                    = (Expression)new GroupByResultExpression(
                                        outerSelectExpression,
                                        keySelector,
                                        keySelector,
                                        elementSelector,
                                        false);

                                if (IsTranslatable(resultSelector))
                                {
                                    return outerQuery
                                        .UpdateSelectExpression(outerSelectExpression
                                            .UpdateProjection(new ServerProjectionExpression(resultSelector))
                                            .UpdateGrouping(keySelector));
                                }

                                var keyPlaceholderGrouping
                                    = KeyPlaceholderGroupingInjectingExpressionVisitor.CreateKeyPlaceholderGrouping(
                                        outerSelectExpression,
                                        keySelector);

                                var groupingParameter = Expression.Parameter(keyPlaceholderGrouping.Type, "g");

                                return outerQuery
                                    .UpdateSelectExpression(outerSelectExpression
                                        .UpdateProjection(
                                            new ClientProjectionExpression(
                                                new ServerProjectionExpression(keyPlaceholderGrouping),
                                                Expression.Lambda(
                                                    new GroupByResultExpression(
                                                        outerSelectExpression,
                                                        Expression.MakeMemberAccess(
                                                            groupingParameter,
                                                            keyPlaceholderGrouping.Type.GetRuntimeProperty("Key")),
                                                        keySelector,
                                                        elementSelector,
                                                        false),
                                                    groupingParameter)))
                                        .UpdateGrouping(keySelector));
                            }
                        }

                        // Generation operations

                        case nameof(Queryable.DefaultIfEmpty):
                        {
                            if (node.Arguments.Count > 1)
                            {
                                // TODO: See if we can support the default value argument
                                goto ReturnEnumerableCall;
                            }

                            if (!(outerQuery.SelectExpression.Projection is ServerProjectionExpression))
                            {
                                // TODO: See if we can work around this condition
                                goto ReturnEnumerableCall;
                            }

                            var emptySubquery
                                = new SubqueryTableExpression(
                                    alias: "t",
                                    subquery: new SelectExpression(
                                        new ServerProjectionExpression(
                                            EmptyRecord.NewExpression)));

                            var innerProjectionBody = outerQuery.SelectExpression.Projection.Flatten().Body;

                            var innerSubquery
                                = new SubqueryTableExpression(
                                    alias: "t",
                                    subquery: outerQuery.SelectExpression.UpdateProjection(
                                        new ServerProjectionExpression(
                                            new DefaultIfEmptyFlagExpression(
                                                innerProjectionBody))));

                            var joinExpression
                                = new LeftJoinExpression(
                                    emptySubquery,
                                    innerSubquery,
                                    Expression.Equal(Expression.Constant(1), Expression.Constant(1)),
                                    innerSubquery.Type);

                            var projectionBody
                                = new ProjectionReferenceRewritingExpressionVisitor(innerSubquery)
                                    .Visit(innerProjectionBody);

                            var selectExpression
                                = new SelectExpression(
                                    new ServerProjectionExpression(Expression.Lambda(
                                        new DefaultIfEmptyTestExpression(
                                            projectionBody,
                                            innerSubquery))),
                                    joinExpression);

                            return new EnumerableRelationalQueryExpression(selectExpression);
                        }

                        // Element operations

                        case nameof(Queryable.First):
                        {
                            // TODO: Inspect whether the outer query needs to be pushed down

                            var selectExpression = outerQuery.SelectExpression;

                            if (node.Arguments.Count == 2)
                            {
                                var outerProjection = selectExpression.Projection.Flatten().Body;

                                var predicate
                                    = node.Arguments[1]
                                        .UnwrapLambda()
                                        .ExpandParameters(outerProjection)
                                        .VisitWith(PostExpansionVisitors);

                                if (!IsTranslatable(predicate))
                                {
                                    goto ReturnEnumerableCall;
                                }

                                selectExpression = selectExpression.AddToPredicate(predicate);
                            }

                            return Expression.Call(
                                ImpatientExtensions
                                    .GetGenericMethodDefinition((IEnumerable<object> e) => e.First())
                                    .MakeGenericMethod(selectExpression.Type),
                                outerQuery
                                    .UpdateSelectExpression(selectExpression
                                        .UpdateLimit(Expression.Constant(1))));
                        }

                        case nameof(Queryable.FirstOrDefault):
                        {
                            // TODO: Inspect whether the outer query needs to be pushed down
                            // TODO: Let FirstOrDefault run at the server in subqueries

                            var selectExpression = outerQuery.SelectExpression;

                            if (node.Arguments.Count == 2)
                            {
                                var outerProjection = selectExpression.Projection.Flatten().Body;

                                var predicate
                                    = node.Arguments[1]
                                        .UnwrapLambda()
                                        .ExpandParameters(outerProjection)
                                        .VisitWith(PostExpansionVisitors);

                                if (!IsTranslatable(predicate))
                                {
                                    goto ReturnEnumerableCall;
                                }

                                selectExpression = selectExpression.AddToPredicate(predicate);
                            }

                            return Expression.Call(
                                ImpatientExtensions
                                    .GetGenericMethodDefinition((IEnumerable<object> e) => e.FirstOrDefault())
                                    .MakeGenericMethod(selectExpression.Type),
                                outerQuery
                                    .UpdateSelectExpression(selectExpression
                                        .UpdateLimit(Expression.Constant(1))));
                        }

                        case nameof(Queryable.Last):
                        {
                            goto ReturnEnumerableCall;
                        }

                        case nameof(Queryable.LastOrDefault):
                        {
                            goto ReturnEnumerableCall;
                        }

                        case nameof(Queryable.Single):
                        {
                            // TODO: Inspect whether the outer query needs to be pushed down

                            var selectExpression = outerQuery.SelectExpression;

                            if (node.Arguments.Count == 2)
                            {
                                var outerProjection = selectExpression.Projection.Flatten().Body;

                                var predicate
                                    = node.Arguments[1]
                                        .UnwrapLambda()
                                        .ExpandParameters(outerProjection)
                                        .VisitWith(PostExpansionVisitors);

                                if (!IsTranslatable(predicate))
                                {
                                    goto ReturnEnumerableCall;
                                }

                                selectExpression = selectExpression.AddToPredicate(predicate);
                            }

                            return Expression.Call(
                                ImpatientExtensions
                                    .GetGenericMethodDefinition((IEnumerable<object> e) => e.Single())
                                    .MakeGenericMethod(selectExpression.Type),
                                outerQuery
                                    .UpdateSelectExpression(selectExpression
                                        .UpdateLimit(Expression.Constant(2))));
                        }

                        case nameof(Queryable.SingleOrDefault):
                        {
                            // TODO: Inspect whether the outer query needs to be pushed down
                            var selectExpression = outerQuery.SelectExpression;

                            if (node.Arguments.Count == 2)
                            {
                                var outerProjection = selectExpression.Projection.Flatten().Body;

                                var predicate
                                    = node.Arguments[1]
                                        .UnwrapLambda()
                                        .ExpandParameters(outerProjection)
                                        .VisitWith(PostExpansionVisitors);

                                if (!IsTranslatable(predicate))
                                {
                                    goto ReturnEnumerableCall;
                                }

                                selectExpression = selectExpression.AddToPredicate(predicate);
                            }

                            return Expression.Call(
                                ImpatientExtensions
                                    .GetGenericMethodDefinition((IEnumerable<object> e) => e.SingleOrDefault())
                                    .MakeGenericMethod(selectExpression.Type),
                                outerQuery
                                    .UpdateSelectExpression(selectExpression
                                        .UpdateLimit(Expression.Constant(2))));
                        }

                        case nameof(Queryable.ElementAt):
                        {
                            // May not ever be supported

                            goto ReturnEnumerableCall;
                        }

                        case nameof(Queryable.ElementAtOrDefault):
                        {
                            // May not ever be supported

                            goto ReturnEnumerableCall;
                        }

                        // Sorting operations

                        case nameof(Queryable.OrderBy):
                        case nameof(Queryable.OrderByDescending):
                        case nameof(Queryable.ThenBy):
                        case nameof(Queryable.ThenByDescending):
                        {
                            if (node.Arguments.Count == 3)
                            {
                                // TODO: Investigate possibility of supporting IComparer overloads
                                goto ReturnEnumerableCall;
                            }

                            // TODO: Inspect whether the outer query needs to be pushed down

                            var outerProjection = outerQuery.SelectExpression.Projection.Flatten().Body;

                            var ordering
                                = node.Arguments[1]
                                    .UnwrapLambda()
                                    .ExpandParameters(outerProjection)
                                    .VisitWith(PostExpansionVisitors);

                            if (!IsTranslatable(ordering) || !ordering.Type.IsScalarType())
                            {
                                // TODO: Convert the existing OrderByExpression, if any,
                                // into a client method call tree, and replace
                                // arguments[1] with it.

                                goto ReturnEnumerableCall;
                            }

                            var descending
                                = node.Method.Name == nameof(Queryable.OrderByDescending)
                                    || node.Method.Name == nameof(Queryable.ThenByDescending);

                            return outerQuery
                                .UpdateSelectExpression(outerQuery.SelectExpression
                                    .AddToOrderBy(ordering, descending))
                                .AsOrderedQueryable();
                        }

                        case nameof(Queryable.Reverse):
                        {
                            if (outerQuery.SelectExpression.OrderBy == null)
                            {
                                goto ReturnEnumerableCall;
                            }

                            // TODO: Inspect whether the outer query needs to be pushed down

                            return outerQuery
                                .UpdateSelectExpression(outerQuery.SelectExpression
                                    .UpdateOrderBy(outerQuery.SelectExpression.OrderBy.Reverse()));
                        }

                        // Partitioning operations

                        case nameof(Queryable.Take):
                        {
                            var outerSelectExpression = outerQuery.SelectExpression;
                            var outerProjection = outerSelectExpression.Projection.Flatten().Body;

                            if (outerSelectExpression.IsDistinct
                                || outerSelectExpression.Limit != null)
                            {
                                if (!IsTranslatable(outerProjection))
                                {
                                    goto ReturnEnumerableCall;
                                }

                                Pushdown(
                                    "t",
                                    ref outerSelectExpression,
                                    ref outerProjection,
                                    out _);
                            }

                            var count = Visit(node.Arguments[1]);

                            if (count is ConstantExpression)
                            {
                                return outerQuery
                                    .UpdateSelectExpression(outerSelectExpression
                                        .UpdateLimit(count));
                            }

                            goto ReturnEnumerableCall;
                        }

                        case nameof(Queryable.Skip):
                        {
                            var outerSelectExpression = outerQuery.SelectExpression;
                            var outerProjection = outerSelectExpression.Projection.Flatten().Body;

                            if (outerSelectExpression.IsDistinct
                                || outerSelectExpression.Offset != null
                                || outerSelectExpression.Limit != null)
                            {
                                if (!IsTranslatable(outerProjection))
                                {
                                    goto ReturnEnumerableCall;
                                }

                                Pushdown(
                                    "t",
                                    ref outerSelectExpression,
                                    ref outerProjection,
                                    out _);
                            }

                            var count = Visit(node.Arguments[1]);

                            if (count is ConstantExpression)
                            {
                                return outerQuery
                                    .UpdateSelectExpression(outerSelectExpression
                                        .UpdateOffset(count));
                            }

                            goto ReturnEnumerableCall;
                        }

                        case nameof(Queryable.TakeWhile):
                        {
                            // May not ever be supported

                            goto ReturnEnumerableCall;
                        }

                        case nameof(Queryable.SkipWhile):
                        {
                            // May not ever be supported

                            goto ReturnEnumerableCall;
                        }

                        // Conversion operations

                        case nameof(Queryable.Cast):
                        {
                            var inType = outerQuery.SelectExpression.Projection.Type;
                            var outType = node.Method.GetGenericArguments()[0];

                            if (inType == outType)
                            {
                                return outerQuery;
                            }

                            // TODO: Any other casting scenarios?

                            goto ReturnEnumerableCall;
                        }

                        // Set operations

                        case nameof(Queryable.Distinct):
                        {
                            if (node.Arguments.Count != 1)
                            {
                                // TODO: Investigate possibility of supporting IEqualityComparer overloads
                                goto ReturnEnumerableCall;
                            }
                            
                            var outerSelectExpression = outerQuery.SelectExpression;
                            var outerProjection = outerSelectExpression.Projection.Flatten().Body;

                            if (outerSelectExpression.IsDistinct
                                || outerSelectExpression.Limit != null
                                || outerSelectExpression.Offset != null)
                            {
                                if (!IsTranslatable(outerProjection))
                                {
                                    goto ReturnEnumerableCall;
                                }

                                Pushdown(
                                    "t",
                                    ref outerSelectExpression,
                                    ref outerProjection,
                                    out _);
                            }

                            return outerQuery
                                .UpdateSelectExpression(outerSelectExpression
                                    .AsDistinct());
                        }

                        case nameof(Queryable.Concat):
                        case nameof(Queryable.Except):
                        case nameof(Queryable.Intersect):
                        case nameof(Queryable.Union):
                        {
                            var innerSource = visitedArguments[1] = ProcessQuerySource(Visit(node.Arguments[1]));

                            if (!(innerSource is EnumerableRelationalQueryExpression innerQuery))
                            {
                                goto ReturnEnumerableCall;
                            }

                            var outerProjection = outerQuery.SelectExpression.Projection.Flatten().Body;
                            var innerProjection = innerQuery.SelectExpression.Projection.Flatten().Body;

                            if (!IsTranslatable(outerProjection) || !IsTranslatable(innerProjection))
                            {
                                goto ReturnEnumerableCall;
                            }

                            // TODO: Constraints on ordering

                            var setOperatorExpression = default(SetOperatorExpression);

                            switch (node.Method.Name)
                            {
                                case nameof(Queryable.Concat):
                                {
                                    setOperatorExpression = new UnionAllExpression(outerQuery.SelectExpression, innerQuery.SelectExpression);
                                    break;
                                }

                                case nameof(Queryable.Except):
                                {
                                    setOperatorExpression = new ExceptExpression(outerQuery.SelectExpression, innerQuery.SelectExpression);
                                    break;
                                }

                                case nameof(Queryable.Intersect):
                                {
                                    setOperatorExpression = new IntersectExpression(outerQuery.SelectExpression, innerQuery.SelectExpression);
                                    break;
                                }

                                case nameof(Queryable.Union):
                                {
                                    setOperatorExpression = new UnionExpression(outerQuery.SelectExpression, innerQuery.SelectExpression);
                                    break;
                                }
                            }

                            var projectionBody
                                = new ProjectionReferenceRewritingExpressionVisitor(setOperatorExpression)
                                    .Visit(outerProjection);

                            return new EnumerableRelationalQueryExpression(
                                new SelectExpression(
                                    new ServerProjectionExpression(projectionBody),
                                    setOperatorExpression));
                        }

                        case nameof(Queryable.Zip):
                        {
                            // May not ever be supported

                            goto ReturnEnumerableCall;
                        }

                        // Equality operations

                        case nameof(Queryable.SequenceEqual):
                        {
                            // May not ever be supported

                            goto ReturnEnumerableCall;
                        }

                        // Quantifier operations

                        case nameof(Queryable.All):
                        {
                            var outerSelectExpression = outerQuery.SelectExpression;
                            var outerProjection = outerSelectExpression.Projection.Flatten().Body;

                            // TODO: Inspect whether the outer query needs to be pushed down

                            var predicate
                                = node.Arguments[1]
                                    .UnwrapLambda()
                                    .ExpandParameters(outerProjection)
                                    .VisitWith(PostExpansionVisitors);

                            if (!IsTranslatable(predicate))
                            {
                                goto ReturnEnumerableCall;
                            }

                            // TODO: Consider another approach here (CASE WHEN EXISTS (... EXCEPT ... WHERE) or something)

                            var conditional
                                = new SqlCastExpression(
                                    Expression.Condition(
                                        Expression.Equal(
                                            new SqlFragmentExpression("COUNT_BIG(*)", typeof(long)),
                                            new SqlAggregateExpression(
                                                "SUM",
                                                Expression.Condition(
                                                    predicate,
                                                    Expression.Constant(1),
                                                    Expression.Constant(0)),
                                                typeof(long))),
                                        Expression.Constant(true),
                                        Expression.Constant(false)),
                                    "BIT",
                                    typeof(bool));

                            return new SingleValueRelationalQueryExpression(
                                outerSelectExpression.UpdateProjection(
                                    new ServerProjectionExpression(
                                        conditional)));
                        }

                        case nameof(Queryable.Any):
                        {
                            // TODO: Inspect whether the outer query needs to be pushed down

                            var outerSelectExpression = outerQuery.SelectExpression;
                            var outerProjection = outerSelectExpression.Projection.Flatten().Body;

                            if (node.Arguments.Count == 2)
                            {
                                var predicateBody
                                    = node.Arguments[1]
                                        .UnwrapLambda()
                                        .ExpandParameters(outerProjection)
                                        .VisitWith(PostExpansionVisitors);

                                if (!IsTranslatable(predicateBody))
                                {
                                    goto ReturnEnumerableCall;
                                }

                                outerSelectExpression = outerSelectExpression.AddToPredicate(predicateBody);
                            }

                            if (!IsTranslatable(outerProjection))
                            {
                                goto ReturnEnumerableCall;
                            }

                            var subquery
                                = outerSelectExpression.UpdateProjection(
                                    new ServerProjectionExpression(
                                        Expression.Constant(1)));

                            var existsQueryBody
                                = new SqlCastExpression(
                                    Expression.Condition(
                                        new SqlExistsExpression(subquery),
                                        Expression.Constant(true),
                                        Expression.Constant(false)),
                                    "BIT",
                                    typeof(bool));

                            return new SingleValueRelationalQueryExpression(
                                new SelectExpression(
                                    new ServerProjectionExpression(
                                        existsQueryBody)));
                        }

                        case nameof(Queryable.Contains):
                        {
                            var outerSelectExpression = outerQuery.SelectExpression;
                            var outerProjection = outerSelectExpression.Projection.Flatten().Body;

                            if (!IsTranslatable(outerProjection))
                            {
                                goto ReturnEnumerableCall;
                            }

                            var valuesExpression = Visit(node.Arguments[1]);

                            switch (valuesExpression)
                            {
                                case SingleValueRelationalQueryExpression singleValueRelationalQueryExpression:
                                {
                                    // TODO: Review this
                                    throw new NotImplementedException();
                                }

                                case ConstantExpression constantExpression:
                                {
                                    return new SingleValueRelationalQueryExpression(
                                        new SelectExpression(
                                            new ServerProjectionExpression(
                                                new SqlCastExpression(
                                                    Expression.Condition(
                                                        new SqlInExpression(constantExpression, outerSelectExpression),
                                                        Expression.Constant(true),
                                                        Expression.Constant(false)),
                                                    "BIT",
                                                    typeof(bool)))));
                                }
                            }

                            goto ReturnEnumerableCall;
                        }

                        // Aggregation operations

                        case nameof(Queryable.Average):
                        case nameof(Queryable.Max):
                        case nameof(Queryable.Min):
                        case nameof(Queryable.Sum):
                        {
                            var outerSelectExpression = outerQuery.SelectExpression;
                            var outerProjection = outerSelectExpression.Projection.Flatten().Body;

                            if (outerSelectExpression.IsDistinct
                                || outerSelectExpression.Limit != null
                                || outerSelectExpression.Offset != null)
                            {
                                if (!IsTranslatable(outerProjection))
                                {
                                    goto ReturnEnumerableCall;
                                }

                                Pushdown(
                                    "t",
                                    ref outerSelectExpression,
                                    ref outerProjection,
                                    out _);
                            }

                            if (node.Arguments.Count == 2)
                            {
                                var selectorLambda = node.Arguments[1].UnwrapLambda();

                                outerProjection
                                    = selectorLambda
                                        .ExpandParameters(outerProjection)
                                        .VisitWith(PostExpansionVisitors);

                                if (!IsTranslatable(outerProjection))
                                {
                                    goto ReturnEnumerableCall;
                                }
                                else if (outerProjection is SingleValueRelationalQueryExpression singleValueRelationalQueryExpression
                                    && singleValueRelationalQueryExpression.SelectExpression.Projection.Flatten().Body is SqlAggregateExpression)
                                {
                                    var method = node.Method;

                                    if (method.Name == "Sum")
                                    {
                                        method = typeof(Enumerable).GetRuntimeMethod("Sum", new[] { typeof(IEnumerable<>).MakeGenericType(outerProjection.Type) });
                                    }
                                    else if (method.Name == "Average")
                                    {
                                        method = typeof(Enumerable).GetRuntimeMethod("Average", new[] { typeof(IEnumerable<>).MakeGenericType(outerProjection.Type) });
                                    }
                                    else if (method.Name == "Min")
                                    {
                                        method = GetGenericMethodDefinition((IEnumerable<int> e) => e.Min()).MakeGenericMethod(outerProjection.Type);
                                    }
                                    else if (method.Name == "Max")
                                    {
                                        method = GetGenericMethodDefinition((IEnumerable<int> e) => e.Max()).MakeGenericMethod(outerProjection.Type);
                                    }

                                    return Expression.Call(
                                        method,
                                        outerQuery
                                            .UpdateSelectExpression(outerSelectExpression
                                                .UpdateProjection(new ServerProjectionExpression(
                                                    outerProjection))));
                                }
                            }
                            else if (!IsTranslatable(outerProjection)
                                || (outerProjection is SingleValueRelationalQueryExpression singleValueRelationalQueryExpression
                                    && singleValueRelationalQueryExpression.SelectExpression.Projection.Flatten().Body is SqlAggregateExpression))
                            {
                                goto ReturnEnumerableCall;
                            }

                            var sqlFunctionExpression
                                = node.Method.Name == nameof(Queryable.Average)
                                    ? new SqlFunctionExpression(
                                        "AVG",
                                        node.Method.ReturnType,
                                        new SqlCastExpression(
                                            outerProjection,
                                            outerProjection.Type == typeof(decimal)
                                                || outerProjection.Type == typeof(decimal?)
                                                ? "decimal"
                                                : "float",
                                            outerProjection.Type))
                                    : new SqlFunctionExpression(
                                        node.Method.Name.ToUpperInvariant(),
                                        outerProjection.Type,
                                        outerProjection);

                            return new SingleValueRelationalQueryExpression(
                                outerSelectExpression
                                    .UpdateProjection(new ServerProjectionExpression(
                                        sqlFunctionExpression)));
                        }

                        case nameof(Queryable.Count):
                        case nameof(Queryable.LongCount):
                        {
                            var outerSelectExpression = outerQuery.SelectExpression;
                            var outerProjection = outerSelectExpression.Projection.Flatten().Body;

                            if (outerSelectExpression.IsDistinct
                                || outerSelectExpression.Limit != null
                                || outerSelectExpression.Offset != null)
                            {
                                Pushdown(
                                    "t",
                                    ref outerSelectExpression,
                                    ref outerProjection,
                                    out _);
                            }

                            if (node.Arguments.Count == 2)
                            {
                                var predicate
                                    = node.Arguments[1]
                                        .UnwrapLambda()
                                        .ExpandParameters(outerProjection)
                                        .VisitWith(PostExpansionVisitors);

                                if (!IsTranslatable(predicate))
                                {
                                    goto ReturnEnumerableCall;
                                }

                                outerSelectExpression = outerSelectExpression.AddToPredicate(predicate);
                            }

                            var starFragment = new SqlFragmentExpression("*", typeof(object));

                            return new SingleValueRelationalQueryExpression(
                                outerSelectExpression
                                    .UpdateProjection(new ServerProjectionExpression(
                                        node.Method.Name == nameof(Queryable.Count)
                                            ? new SqlAggregateExpression("COUNT", starFragment, typeof(int))
                                            : new SqlAggregateExpression("COUNT_BIG", starFragment, typeof(long)))));
                        }

                        case nameof(Queryable.Aggregate):
                        {
                            // May not ever be supported

                            goto ReturnEnumerableCall;
                        }
                    }
                }

                ReturnEnumerableCall:

                return Expression.Call(
                    MatchQueryableMethod(node.Method),
                    node.Arguments
                        .Zip(visitedArguments, (original, visited) => visited ?? Visit(original))
                        .Select(a => a.NodeType == ExpressionType.Quote ? a.UnwrapLambda() : a));
            }

            return base.VisitMethodCall(node);
        }

        private bool IsTranslatable(Expression node)
            => expressionVisitorProvider.TranslatabilityAnalyzingExpressionVisitor
                .Visit(node) is TranslatableExpression;

        private void Pushdown(
            string alias,
            ref SelectExpression selectExpression,
            ref Expression projection,
            out TableExpression table)
        {
            table
                = new SubqueryTableExpression(
                    keyPlaceholderGroupingInjector.VisitAndConvert(selectExpression, nameof(VisitMethodCall)),
                    alias.StartsWith("<>") ? "t" : alias);

            projection
                = new ProjectionReferenceRewritingExpressionVisitor((SubqueryTableExpression)table)
                    .Visit(projection)
                    .VisitWith(PostExpansionVisitors);

            selectExpression
                = new SelectExpression(
                    new ServerProjectionExpression(projection),
                    table);
        }

        private static Expression ProcessQuerySource(Expression node)
        {
            switch (node)
            {
                case GroupedRelationalQueryExpression groupedRelationalQueryExpression:
                {
                    return new EnumerableRelationalQueryExpression(
                        groupedRelationalQueryExpression.SelectExpression
                            .AddToPredicate(Expression.Equal(
                                groupedRelationalQueryExpression.OuterKeySelector,
                                groupedRelationalQueryExpression.InnerKeySelector)));
                }

                default:
                {
                    return node;
                }
            }
        }

        private static MethodInfo MatchQueryableMethod(MethodInfo method)
        {
            if (method.DeclaringType == typeof(Enumerable))
            {
                return method;
            }

            var genericMethodDefinition = method.GetGenericMethodDefinition();

            var genericArguments = genericMethodDefinition.GetGenericArguments();

            var parameterTypes
                = genericMethodDefinition.GetParameters()
                    .Select(p =>
                    {
                        if (p.ParameterType.IsConstructedGenericType)
                        {
                            var genericTypeDefinition = p.ParameterType.GetGenericTypeDefinition();

                            if (genericTypeDefinition == typeof(Expression<>))
                            {
                                return p.ParameterType.GenericTypeArguments[0];
                            }
                            else if (genericTypeDefinition == typeof(IQueryable<>))
                            {
                                return typeof(IEnumerable<>).MakeGenericType(p.ParameterType.GenericTypeArguments[0]);
                            }
                        }

                        return p.ParameterType;
                    })
                    .ToArray();

            bool TypesMatch(Type type1, Type type2)
            {
                if (type1 == type2)
                {
                    return true;
                }
                else if (type1.IsConstructedGenericType && type2.IsConstructedGenericType)
                {
                    var genericType1 = type1.GetGenericTypeDefinition();
                    var genericType2 = type2.GetGenericTypeDefinition();

                    return genericType1 == genericType2
                        && type1.GenericTypeArguments.Zip(type2.GenericTypeArguments, TypesMatch).All(b => b);
                }
                else if (type1.IsGenericParameter && type2.IsGenericParameter)
                {
                    return type1.Name == type2.Name
                        && type1.GenericParameterPosition == type2.GenericParameterPosition;
                }
                else
                {
                    return false;
                }
            }

            var matching = (from m in typeof(Enumerable).GetTypeInfo().DeclaredMethods

                            where m.Name == method.Name

                            let parameters = m.GetParameters()
                            where parameters.Length == parameterTypes.Length
                            where m.GetParameters().Select(p => p.ParameterType).Zip(parameterTypes, TypesMatch).All(b => b)

                            let arguments = m.GetGenericArguments()
                            where arguments.Length == genericArguments.Length
                            where arguments.Zip(genericArguments, TypesMatch).All(b => b)

                            select m).ToList();

            return matching.Single().MakeGenericMethod(method.GetGenericArguments());
        }

        private static readonly MethodInfo groupByKeyElement
            = GetGenericMethodDefinition((IQueryable<object> q) => q.GroupBy(x => x, x => x));

        private static readonly MethodInfo groupByKeyResult
            = GetGenericMethodDefinition((IQueryable<object> q) => q.GroupBy(x => x, (x, y) => x));

        private static readonly MethodInfo groupByKeyElementResult
            = GetGenericMethodDefinition((IQueryable<object> q) => q.GroupBy(x => x, x => x, (x, y) => x));

        private class EmptyRecord
        {
            private EmptyRecord(string empty)
            {
                Empty = empty;
            }

            public string Empty { get; }

            public static readonly NewExpression NewExpression
                = Expression.New(
                    typeof(EmptyRecord).GetTypeInfo().DeclaredConstructors.Single(c => !c.IsStatic),
                    new[] { Expression.Constant(null, typeof(string)) },
                    new[] { typeof(EmptyRecord).GetRuntimeProperty(nameof(Empty)) });
        }
    }
}