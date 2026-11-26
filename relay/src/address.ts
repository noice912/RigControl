/**
 * The rate-limit key of a client address (CF-Connecting-IP): IPv4 as is, IPv6 by its /64 (one home or phone
 * network gets a whole /64, so counting single IPv6 addresses would limit nothing).
 */
export function addressKey(ip: string): string {
  if (!ip.includes(":")) return `ip|${ip}`;
  const [head = "", tail = ""] = ip.toLowerCase().split("::", 2);
  const h = head ? head.split(":") : [];
  const t = ip.includes("::") ? (tail ? tail.split(":") : []) : [];
  const groups = ip.includes("::") ? [...h, ...Array(Math.max(0, 8 - h.length - t.length)).fill("0"), ...t] : h;
  return `ip6|${groups.slice(0, 4).map((g) => g.replace(/^0+(?=.)/, "")).join(":")}`;
}
