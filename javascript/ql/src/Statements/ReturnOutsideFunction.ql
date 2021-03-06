/**
 * @name Return statement outside function
 * @description A 'return' statement appearing outside a function will cause an error
 *              when it is executed.
 * @kind problem
 * @problem.severity warning
 * @id js/return-outside-function
 * @tags reliability
 *       correctness
 * @precision medium
 */

import javascript
import semmle.javascript.RestrictedLocations

from ReturnStmt ret, TopLevel tl
where tl = ret.getContainer() and
      not tl instanceof EventHandlerCode and
      not tl instanceof NodeModule
select (FirstLineOf)ret, "Return statement outside function."