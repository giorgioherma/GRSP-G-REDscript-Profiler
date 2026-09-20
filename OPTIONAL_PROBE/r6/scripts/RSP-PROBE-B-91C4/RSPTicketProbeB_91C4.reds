// RSP_OWNER_TICKET: RSP-PROBE-B-91C4

@addField(PlayerPuppet)
private let m_rspProbeB_91C4: Int32;

@addMethod(PlayerPuppet)
private func RSPProbeBMarker_91C4() -> Void {
    this.m_rspProbeB_91C4 = 914;
}

@wrapMethod(PlayerPuppet)
protected cb func OnGameAttached() -> Bool {
    let result = wrappedMethod();
    this.RSPProbeBMarker_91C4();
    this.RSPProbeAWrite_7F2A(722);
    return result;
}
