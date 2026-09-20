// RSP_OWNER_TICKET: RSP-PROBE-C-D3E8

@addField(PlayerPuppet)
private let m_rspProbeC_D3E8: Int32;

@addMethod(PlayerPuppet)
private func RSPProbeCMarker_D3E8() -> Void {
    this.m_rspProbeC_D3E8 = 338;
}

@wrapMethod(PlayerPuppet)
protected cb func OnGameAttached() -> Bool {
    let result = wrappedMethod();
    this.RSPProbeCMarker_D3E8();
    return result;
}
