﻿using System.Collections.Generic;
using System.Data.Common;
using System.Data.Entity.Core.Common.CommandTrees;
using System.Data.Entity.Core.Metadata.Edm;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;

namespace System.Data.SQLite.EF6.Migrations
{
    /// <summary>
    ///     Class generating SQL for a DML command tree.
    /// </summary>
    internal static class SQLiteDmlBuilder
    {
        private const  int  COMMANDTEXT_STRINGBUILDER_INITIALCAPACITY = 256;
        private static Guid _lastGuid;

        internal static string GenerateDeleteSql(DbDeleteCommandTree tree, out List<DbParameter> parameters,
                                                 bool                insertParametersValuesInSql = false)
        {
            var commandText = new StringBuilder(COMMANDTEXT_STRINGBUILDER_INITIALCAPACITY);
            var translator  = new ExpressionTranslator(commandText, tree, false, insertParametersValuesInSql);

            commandText.Append("delete from ");
            tree.Target.Expression.Accept(translator);
            commandText.AppendLine();

            // where c1 = ... AND c2 = ...
            commandText.Append("where ");
            tree.Predicate.Accept(translator);

            parameters = translator.Parameters;
            return commandText.ToString();
        }

        internal static string GenerateInsertSql(DbInsertCommandTree tree, out List<DbParameter> parameters,
                                                 bool                insertParametersValuesInSql = false)
        {
            var commandText = new StringBuilder(COMMANDTEXT_STRINGBUILDER_INITIALCAPACITY);
            var translator =
                new ExpressionTranslator(commandText, tree, tree.Returning != null, insertParametersValuesInSql);

            commandText.Append("insert into ");
            tree.Target.Expression.Accept(translator);

            // Actually is not possible to retrieve the last inserted guid from Access
            // We can understand if there is a guid checking the returning value of the insert
            // statement
            string guidAutogeneratedColumn = null;
            if (tree.Returning is DbNewInstanceExpression)
            {
                guidAutogeneratedColumn = GetGuidArgs(tree.Returning as DbNewInstanceExpression);
            }

            if (tree.SetClauses.Count != 0)
            {
                var first = true;

                // (c1, c2, c3, ...)
                commandText.Append("(");
                if (!string.IsNullOrEmpty(guidAutogeneratedColumn))
                {
                    commandText.Append(SQLiteProviderManifestHelper.QuoteIdentifier(guidAutogeneratedColumn));
                    first = false;
                }

                foreach (DbSetClause setClause in tree.SetClauses)
                {
                    if (first)
                    {
                        first = false;
                    }
                    else
                    {
                        commandText.Append(", ");
                    }

                    setClause.Property.Accept(translator);
                }

                commandText.AppendLine(")");

                // values c1, c2, ...
                first = true;
                commandText.Append("values (");
                if (!string.IsNullOrEmpty(guidAutogeneratedColumn))
                {
                    _lastGuid = Guid.NewGuid();
                    commandText.Append(string.Format("{{{0}}}", _lastGuid));
                    first = false;
                }

                foreach (DbSetClause setClause in tree.SetClauses)
                {
                    if (first)
                    {
                        first = false;
                    }
                    else
                    {
                        commandText.Append(", ");
                    }

                    setClause.Value.Accept(translator);

                    translator.RegisterMemberValue(setClause.Property, setClause.Value);
                }

                commandText.AppendLine(");");
            }
            else
            {
                commandText.AppendLine(" default values;");
            }

            // generate returning sql
            GenerateReturningSql(commandText, tree, translator, tree.Returning);

            parameters = translator.Parameters;
            return commandText.ToString();
        }

        /// <summary>
        ///     Generates SQL fragment returning server-generated values.
        ///     Requires: translator knows about member values so that we can figure out
        ///     how to construct the key predicate.
        ///     <code>
        /// Sample SQL:
        ///
        ///     select IdentityValue
        ///     from dbo.MyTable
        ///     where IdentityValue = @@Identity
        ///
        /// </code>
        /// </summary>
        /// <param name="commandText">Builder containing command text</param>
        /// <param name="tree">Modification command tree</param>
        /// <param name="translator">
        ///     Translator used to produce DML SQL statement
        ///     for the tree
        /// </param>
        /// <param name="returning">
        ///     Returning expression. If null, the method returns
        ///     immediately without producing a SELECT statement.
        /// </param>
        private static void GenerateReturningSql(StringBuilder        commandText, DbModificationCommandTree tree,
                                                 ExpressionTranslator translator,  DbExpression              returning)
        {
            // Nothing to do if there is no Returning expression
            if (returning == null)
            {
                return;
            }

            // select
            commandText.Append("select ");
            returning.Accept(translator);
            commandText.AppendLine();

            // from
            commandText.Append("from ");
            tree.Target.Expression.Accept(translator);
            commandText.AppendLine();

            // where
            commandText.Append("where ");
            var table = ((DbScanExpression) tree.Target.Expression).Target;

            var identity = false;
            var first    = true;

            foreach (var keyMember in table.ElementType.KeyMembers)
            {
                if (first)
                {
                    first = false;
                }
                else
                {
                    commandText.Append(" and ");
                }

                commandText.Append(SQLiteProviderManifestHelper.QuoteIdentifier(keyMember.Name));
                commandText.Append(" = ");

                // retrieve member value sql. the translator remembers member values
                // as it constructs the DML statement (which precedes the "returning"
                // SQL)
                DbParameter value;
                if (translator.MemberValues.TryGetValue(keyMember, out value))
                {
                    commandText.Append(value.ParameterName);
                }
                else
                {
                    // if no value is registered for the key member, it means it is an identity
                    // which can be retrieved using the @@identity function
                    if (identity)
                    {
                        // there can be only one server generated key
                        throw new
                            NotSupportedException(string
                                                      .Format("Server generated keys are only supported for identity columns. More than one key column is marked as server generated in table '{0}'.",
                                                              table.Name));
                    }

                    if (keyMember.TypeUsage.EdmType.Name.ToLower() == "guid")

                        // We can't retrieve the latest inserted guid from Access
                        //commandText.Append("@@guid");
                    {
                        commandText.AppendFormat("{{{0}}}", _lastGuid);
                    }
                    else
                    {
                        commandText.Append("@@identity");
                    }

                    identity = true;
                }
            }
        }

        internal static string GenerateUpdateSql(DbUpdateCommandTree tree, out List<DbParameter> parameters,
                                                 bool                insertParametersValuesInSql = false)
        {
            var commandText = new StringBuilder(COMMANDTEXT_STRINGBUILDER_INITIALCAPACITY);
            var translator =
                new ExpressionTranslator(commandText, tree, tree.Returning != null, insertParametersValuesInSql);

            commandText.Append("update ");
            tree.Target.Expression.Accept(translator);
            commandText.AppendLine();

            // set c1 = ..., c2 = ..., ...
            var first = true;
            commandText.Append("set ");
            foreach (DbSetClause setClause in tree.SetClauses)
            {
                if (first)
                {
                    first = false;
                }
                else
                {
                    commandText.Append(", ");
                }

                setClause.Property.Accept(translator);
                commandText.Append(" = ");
                setClause.Value.Accept(translator);
            }

            if (first)
            {
                // If first is still true, it indicates there were no set
                // clauses. Introduce a fake set clause so that:
                // - we acquire the appropriate locks
                // - server-gen columns (e.g. timestamp) get recomputed
                //
                // We use the following pattern:
                //
                //  update Foo
                //  set @i = 0
                //  where ...
                DbParameter parameter =
                    translator.CreateParameter(
                                               default(int),
                                               TypeUsage.CreateDefaultTypeUsage(PrimitiveType
                                                                                    .GetEdmPrimitiveType(PrimitiveTypeKind
                                                                                                             .Int32)));

                commandText.Append(parameter.ParameterName);
                commandText.Append(" = 0");
            }

            commandText.AppendLine();

            // where c1 = ... AND c2 = ...
            commandText.Append("where ");
            tree.Predicate.Accept(translator);
            commandText.AppendLine();

            // generate returning sql
            GenerateReturningSql(commandText, tree, translator, tree.Returning);

            parameters = translator.Parameters;
            return commandText.ToString();
        }

        private static string GetGuidArgs(DbNewInstanceExpression returning)
        {
            return (from arg in returning.Arguments
                    where ((DbPropertyExpression) arg).Property.IsStoreGeneratedIdentity
                          && ((DbPropertyExpression) arg).Property.TypeUsage.EdmType.Name == "guid"
                    select ((DbPropertyExpression) arg).Property.Name
                   ).FirstOrDefault() ?? string.Empty;
        }

        /// <summary>
        ///     Gets escaped TSql identifier describing this entity set.
        /// </summary>
        /// <returns></returns>
        internal static string GetTargetTSql(EntitySetBase entitySetBase)
        {
            // construct escaped T-SQL referencing entity set
            var builder       = new StringBuilder(50);
            var definingQuery = MetadataHelpers.TryGetValueForMetadataProperty<string>(entitySetBase, "DefiningQuery");
            if (!string.IsNullOrEmpty(definingQuery))
            {
                builder.Append("(");
                builder.Append(definingQuery);
                builder.Append(")");
            }
            else
            {
                var tableName = MetadataHelpers.TryGetValueForMetadataProperty<string>(entitySetBase, "Table");
                if (!string.IsNullOrEmpty(tableName))
                {
                    builder.Append(SQLiteProviderManifestHelper.QuoteIdentifier(tableName));
                }
                else
                {
                    builder.Append(SQLiteProviderManifestHelper.QuoteIdentifier(entitySetBase.Name));
                }
            }

            return builder.ToString();
        }

        /// <summary>
        ///     Lightweight expression translator for DML expression trees, which have constrained
        ///     scope and support.
        /// </summary>
        private class ExpressionTranslator : DbExpressionVisitor
        {
            internal List<DbParameter> Parameters { get; }

            internal Dictionary<EdmMember, DbParameter> MemberValues { get; }

            private readonly StringBuilder _commandText;
            private readonly bool          _insertParametersValuesInSql;

            private int _parameterNameCount;

            /// <summary>
            ///     Initialize a new expression translator populating the given string builder
            ///     with command text. Command text builder and command tree must not be null.
            /// </summary>
            /// <param name="commandText">Command text with which to populate commands</param>
            /// <param name="commandTree">Command tree generating SQL</param>
            /// <param name="preserveMemberValues">
            ///     Indicates whether the translator should preserve
            ///     member values while compiling sql expression
            /// </param>
            /// <param name="insertParametersValuesInSql">
            ///     if set to <c>true</c> parameters values are inserted directly in SQL
            ///     statement.
            /// </param>
            internal ExpressionTranslator(StringBuilder commandText, DbModificationCommandTree commandTree,
                                          bool          preserveMemberValues,
                                          bool          insertParametersValuesInSql)
            {
                Debug.Assert(commandText != null);
                Debug.Assert(commandTree != null);

                this._commandText = commandText;
                this.Parameters   = new List<DbParameter>();
                this.MemberValues =
                    preserveMemberValues ? new Dictionary<EdmMember, DbParameter>() : null;
                this._insertParametersValuesInSql = insertParametersValuesInSql;
            }

            public override void Visit(DbAndExpression expression)
            {
                this.VisitBinary(expression, " and ");
            }

            public override void Visit(DbOrExpression expression)
            {
                this.VisitBinary(expression, " or ");
            }

            public override void Visit(DbComparisonExpression expression)
            {
                Debug.Assert(expression.ExpressionKind == DbExpressionKind.Equals,
                             "only equals comparison expressions are produced in DML command trees in V1");

                this.VisitBinary(expression, " = ");

                this.RegisterMemberValue(expression.Left, expression.Right);
            }

            public override void Visit(DbIsNullExpression expression)
            {
                expression.Argument.Accept(this);
                this._commandText.Append(" is null");
            }

            public override void Visit(DbNotExpression expression)
            {
                this._commandText.Append("not (");
                expression.Accept(this);
                this._commandText.Append(")");
            }

            public override void Visit(DbConstantExpression expression)
            {
                if (this._insertParametersValuesInSql)
                {
                    this._commandText.Append(this.VisitConstantExpression(expression));
                }
                else
                {
                    var parameter = this.CreateParameter(expression.Value, expression.ResultType);
                    this._commandText.Append(parameter.ParameterName);
                }
            }

            public override void Visit(DbScanExpression expression)
            {
                this._commandText.Append(GetTargetTSql(expression.Target));
            }

            public override void Visit(DbPropertyExpression expression)
            {
                this._commandText.Append(SQLiteProviderManifestHelper.QuoteIdentifier(expression.Property.Name));
            }

            public override void Visit(DbNullExpression expression)
            {
                this._commandText.Append("null");
            }

            public override void Visit(DbNewInstanceExpression expression)
            {
                // assumes all arguments are self-describing (no need to use aliases
                // because no renames are ever used in the projection)
                var first = true;
                foreach (var argument in expression.Arguments)
                {
                    if (first)
                    {
                        first = false;
                    }
                    else
                    {
                        this._commandText.Append(", ");
                    }

                    argument.Accept(this);
                }
            }

            // generate parameter (name based on parameter ordinal)
            internal SQLiteParameter CreateParameter(object value, TypeUsage type)
            {
                var parameter = SQLiteProviderServicesHelper.CreateSQLiteParameter(
                                                                                   string.Concat("@p",
                                                                                                 this
                                                                                                     ._parameterNameCount
                                                                                                     .ToString(CultureInfo
                                                                                                                   .InvariantCulture)),
                                                                                   type,
                                                                                   ParameterMode.In,
                                                                                   value);

                this._parameterNameCount++;
                this.Parameters.Add(parameter);

                return parameter;
            }

            /// <summary>
            ///     Call this method to register a property value pair so the translator "remembers"
            ///     the values for members of the row being modified. These values can then be used
            ///     to form a predicate for server-generation (based on the key of the row)
            /// </summary>
            /// <param name="propertyExpression">DbExpression containing the column reference (property expression).</param>
            /// <param name="value">DbExpression containing the value of the column.</param>
            internal void RegisterMemberValue(DbExpression propertyExpression, DbExpression value)
            {
                if (this.MemberValues != null)
                {
                    // register the value for this property
                    Debug.Assert(propertyExpression.ExpressionKind == DbExpressionKind.Property,
                                 "DML predicates and setters must be of the form property = value");

                    // get name of left property
                    var property = ((DbPropertyExpression) propertyExpression).Property;

                    // don't track null values
                    if (value.ExpressionKind != DbExpressionKind.Null)
                    {
                        Debug.Assert(value.ExpressionKind == DbExpressionKind.Constant,
                                     "value must either constant or null");

                        // retrieve the last parameter added (which describes the parameter)
                        this.MemberValues[property] = this.Parameters[this.Parameters.Count - 1];
                    }
                }
            }

            private void VisitBinary(DbBinaryExpression expression, string separator)
            {
                this._commandText.Append("(");
                expression.Left.Accept(this);
                this._commandText.Append(separator);
                expression.Right.Accept(this);
                this._commandText.Append(")");
            }

            /// <summary>
            ///     Constants will be send to the store as part of the generated SQL statement, not as parameters
            /// </summary>
            /// <param name="e"></param>
            /// <returns>
            ///     A <see cref="SqlBuilder" />.  Strings are wrapped in single
            ///     quotes and escaped.  Numbers are written literally.
            /// </returns>
            private string VisitConstantExpression(DbConstantExpression e)
            {
                return this.VisitConstantExpression(e.ResultType, e.Value);
            }

            private string VisitConstantExpression(TypeUsage expressionType, object expressionValue)
            {
                var result = new StringBuilder();

                PrimitiveTypeKind typeKind;

                // Model Types can be (at the time of this implementation):
                //      Binary, Boolean, Byte, DateTime, Decimal, Double, Guid, Int16, Int32, Int64,Single, String
                if (expressionType.TryGetPrimitiveTypeKind(out typeKind))
                {
                    switch (typeKind)
                    {
                        case PrimitiveTypeKind.Int32:
                        case PrimitiveTypeKind.Byte:
                            result.Append(expressionValue);
                            break;

                        case PrimitiveTypeKind.Binary:
                            result.Append(LiteralHelpers.ToSqlString((byte[]) expressionValue));
                            break;

                        case PrimitiveTypeKind.Boolean:
                            result.Append(LiteralHelpers.ToSqlString((bool) expressionValue));
                            break;

                        case PrimitiveTypeKind.DateTime:
                            result.Append(LiteralHelpers.SqlDateTime((DateTime) expressionValue));
                            break;

                        case PrimitiveTypeKind.Time:
                            result.Append(LiteralHelpers.SqlDayTime((DateTime) expressionValue));
                            break;

                        case PrimitiveTypeKind.DateTimeOffset:
                            throw new NotImplementedException("Jet does not implement DateTimeOffset");

                        case PrimitiveTypeKind.Decimal:
                            var strDecimal = ((decimal) expressionValue).ToString(CultureInfo.InvariantCulture);

                            // if the decimal value has no decimal part, cast as decimal to preserve type
                            // if the number has precision > int64 max precision, it will be handled as decimal by sql server
                            // and does not need cast. if precision is lest then 20, then cast using Max(literal precision, sql default precision)
                            if (-1 == strDecimal.IndexOf('.') && strDecimal.TrimStart('-').Length < 20)
                            {
                                var              precision = (byte) strDecimal.Length;
                                FacetDescription precisionFacetDescription;

                                if (!expressionType.EdmType.TryGetTypeFacetDescriptionByName("precision",
                                                                                             out
                                                                                             precisionFacetDescription))
                                {
                                    throw new
                                        InvalidOperationException("Decimal primitive type must have Precision facet");
                                }

                                if (precisionFacetDescription.DefaultValue != null)
                                {
                                    precision = Math.Max(precision, (byte) precisionFacetDescription.DefaultValue);
                                }

                                if (precision <= 0)
                                {
                                    throw new InvalidOperationException("Precision must be greater than zero");
                                }

                                result.Append("cast(");
                                result.Append(strDecimal);
                                result.Append(" as decimal(");
                                result.Append(precision.ToString(CultureInfo.InvariantCulture));
                                result.Append("))");
                            }
                            else
                            {
                                result.Append(strDecimal);
                            }

                            break;

                        case PrimitiveTypeKind.Double:
                            result.Append(((double) expressionValue).ToString(CultureInfo.InvariantCulture));
                            break;
                        case PrimitiveTypeKind.Single:
                            result.Append(((float) expressionValue).ToString(CultureInfo.InvariantCulture));
                            break;
                        case PrimitiveTypeKind.Int16:
                        case PrimitiveTypeKind.Int64:
                            result.Append(expressionValue);
                            break;
                        case PrimitiveTypeKind.String:
                            result.Append(LiteralHelpers.ToSqlString(expressionValue as string));
                            break;

                        case PrimitiveTypeKind.Guid:
                            result.Append(LiteralHelpers.ToSqlString((Guid) expressionValue));
                            break;

                        default:
                            // all known scalar types should been handled already.
                            throw new NotSupportedException("Primitive type kind " + typeKind +
                                                            " is not supported by the Jet Provider");
                    }
                }
                else
                {
                    throw new NotSupportedException();
                }

                return result.ToString();
            }

            #region UnsupportedVisitMethods

            public override void Visit(DbApplyExpression expression)
            {
                throw new NotSupportedException("Visit(\"DbApplyExpression\") is not supported.");
            }

            public override void Visit(DbArithmeticExpression expression)
            {
                throw new NotSupportedException("Visit(\"DbArithmeticExpression\") is not supported.");
            }

            public override void Visit(DbCaseExpression expression)
            {
                throw new NotSupportedException("Visit(\"DbCaseExpression\") is not supported.");
            }

            public override void Visit(DbCastExpression expression)
            {
                throw new NotSupportedException("Visit(\"DbCastExpression\") is not supported.");
            }

            public override void Visit(DbCrossJoinExpression expression)
            {
                throw new NotSupportedException("Visit(\"DbCrossJoinExpression\") is not supported.");
            }

            public override void Visit(DbDerefExpression expression)
            {
                throw new NotSupportedException("Visit(\"DbDerefExpression\") is not supported.");
            }

            public override void Visit(DbDistinctExpression expression)
            {
                throw new NotSupportedException("Visit(\"DbDistinctExpression\") is not supported.");
            }

            public override void Visit(DbElementExpression expression)
            {
                throw new NotSupportedException("Visit(\"DbElementExpression\") is not supported.");
            }

            public override void Visit(DbEntityRefExpression expression)
            {
                throw new NotSupportedException("Visit(\"DbEntityRefExpression\") is not supported.");
            }

            public override void Visit(DbExceptExpression expression)
            {
                throw new NotSupportedException("Visit(\"DbExceptExpression\") is not supported.");
            }

            public override void Visit(DbExpression expression)
            {
                throw new NotSupportedException("Visit(\"DbExpression\") is not supported.");
            }

            public override void Visit(DbFilterExpression expression)
            {
                throw new NotSupportedException("Visit(\"DbFilterExpression\") is not supported.");
            }

            public override void Visit(DbFunctionExpression expression)
            {
                throw new NotSupportedException("Visit(\"DbFunctionExpression\") is not supported.");
            }

            public override void Visit(DbGroupByExpression expression)
            {
                throw new NotSupportedException("Visit(\"DbGroupByExpression\") is not supported.");
            }

            public override void Visit(DbIntersectExpression expression)
            {
                throw new NotSupportedException("Visit(\"DbIntersectExpression\") is not supported.");
            }

            public override void Visit(DbIsEmptyExpression expression)
            {
                throw new NotSupportedException("Visit(\"DbIsEmptyExpression\") is not supported.");
            }

            public override void Visit(DbIsOfExpression expression)
            {
                throw new NotSupportedException("Visit(\"DbIsOfExpression\") is not supported.");
            }

            public override void Visit(DbJoinExpression expression)
            {
                throw new NotSupportedException("Visit(\"DbJoinExpression\") is not supported.");
            }

            public override void Visit(DbLikeExpression expression)
            {
                throw new NotSupportedException("Visit(\"DbLikeExpression\") is not supported.");
            }

            public override void Visit(DbLimitExpression expression)
            {
                throw new NotSupportedException("Visit(\"DbLimitExpression\") is not supported.");
            }

            public override void Visit(DbOfTypeExpression expression)
            {
                throw new NotSupportedException("Visit(\"DbOfTypeExpression\") is not supported.");
            }

            public override void Visit(DbParameterReferenceExpression expression)
            {
                throw new NotSupportedException("Visit(\"DbParameterReferenceExpression\") is not supported.");
            }

            public override void Visit(DbProjectExpression expression)
            {
                throw new NotSupportedException("Visit(\"DbProjectExpression\") is not supported.");
            }

            public override void Visit(DbQuantifierExpression expression)
            {
                throw new NotSupportedException("Visit(\"DbQuantifierExpression\") is not supported.");
            }

            public override void Visit(DbRefExpression expression)
            {
                throw new NotSupportedException("Visit(\"DbRefExpression\") is not supported.");
            }

            public override void Visit(DbRefKeyExpression expression)
            {
                throw new NotSupportedException("Visit(\"DbRefKeyExpression\") is not supported.");
            }

            public override void Visit(DbRelationshipNavigationExpression expression)
            {
                throw new NotSupportedException("Visit(\"DbRelationshipNavigationExpression\") is not supported.");
            }

            public override void Visit(DbSkipExpression expression)
            {
                throw new NotSupportedException("Visit(\"DbSkipExpression\") is not supported.");
            }

            public override void Visit(DbSortExpression expression)
            {
                throw new NotSupportedException("Visit(\"DbSortExpression\") is not supported.");
            }

            public override void Visit(DbTreatExpression expression)
            {
                throw new NotSupportedException("Visit(\"DbTreatExpression\") is not supported.");
            }

            public override void Visit(DbUnionAllExpression expression)
            {
                throw new NotSupportedException("Visit(\"DbUnionAllExpression\") is not supported.");
            }

            public override void Visit(DbVariableReferenceExpression expression)
            {
                throw new NotSupportedException("Visit(\"DbVariableReferenceExpression\") is not supported.");
            }

            #endregion UnsupportedVisitMethods
        }
    }
}