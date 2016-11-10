﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GraphView.GremlinTranslationOps.map
{
    internal class GremlinAddEOp: GremlinTranslationOperator
    {
        internal string EdgeLabel;

        public GremlinAddEOp(string label)
        {
            EdgeLabel = label;
        }
        public override GremlinToSqlContext GetContext()
        {
            GremlinToSqlContext inputContext = GetInputContext();
            GremlinEdgeVariable newEdgeVar = new GremlinEdgeVariable();
            inputContext.AddNewVariable(newEdgeVar);
            inputContext.SetCurrVariable(newEdgeVar);
            inputContext.SetDefaultProjection(newEdgeVar);

            return inputContext;
        }
    }
}
