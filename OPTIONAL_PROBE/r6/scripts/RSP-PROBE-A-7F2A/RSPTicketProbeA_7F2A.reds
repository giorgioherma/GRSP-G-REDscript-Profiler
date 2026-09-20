// RSP_OWNER_TICKET: RSP-PROBE-A-7F2A

@addField(PlayerPuppet)
private let m_rspProbeA_7F2A: Int32;

@addMethod(PlayerPuppet)
public func RSPProbeAWrite_7F2A(value: Int32) -> Void {
    this.m_rspProbeA_7F2A = value;
}
