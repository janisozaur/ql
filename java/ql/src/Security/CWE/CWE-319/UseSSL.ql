/**
 * @name Failure to use SSL
 * @description Non-SSL connections can be intercepted by third parties.
 * @kind problem
 * @problem.severity recommendation
 * @precision medium
 * @id java/non-ssl-connection
 * @tags security
 *       external/cwe/cwe-319
 */

import java
import semmle.code.java.security.Encryption

class URLConnection extends RefType {
  URLConnection() {
    this.getAnAncestor().hasQualifiedName("java.net", "URLConnection") and
    not this.hasName("JarURLConnection")
  }
}

class Socket extends RefType {
  Socket() { this.getAnAncestor().hasQualifiedName("java.net", "Socket") }
}

from MethodAccess m, Class c, string type
where
  m.getQualifier().getType() = c and
  (
    (c instanceof URLConnection and type = "connection")
    or
    (c instanceof Socket and type = "socket")
  ) and
  not c instanceof SSLClass and
  (
    m.getMethod().getName() = "getInputStream" or
    m.getMethod().getName() = "getOutputStream"
  )
select m, "Stream using vulnerable non-SSL " + type + "."
