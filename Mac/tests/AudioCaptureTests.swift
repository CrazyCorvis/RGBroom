import Foundation
import CoreAudio

@main struct AudioCaptureTests {
    static func feed(_ capture: Capture, _ samples: [[Float]], channels: [UInt32]) {
        let list = AudioBufferList.allocate(maximumBuffers: samples.count)
        list.unsafeMutablePointer.pointee.mNumberBuffers = UInt32(samples.count)
        var allocations = [UnsafeMutablePointer<Float>]()
        defer { for data in allocations { data.deallocate() }; free(list.unsafeMutablePointer) }
        for i in samples.indices {
            let data = UnsafeMutablePointer<Float>.allocate(capacity: max(1, samples[i].count))
            data.initialize(from: samples[i], count: samples[i].count); allocations.append(data)
            list[i] = AudioBuffer(mNumberChannels: channels[i], mDataByteSize: UInt32(samples[i].count * 4), mData: data)
        }
        capture.receiveAudio(UnsafePointer(list.unsafeMutablePointer))
    }
    static func main() throws {
        let stereo = Capture()
        feed(stereo, [[0.25, -0.5, 0.75, -1]], channels: [2])
        precondition(stereo.position == 2 && stereo.samples[0][0] == 0.25 && stereo.samples[1][1] == -1)
        let planar = Capture()
        feed(planar, [[0.25, 0.75], [-0.5, -1]], channels: [1, 1])
        precondition(stereo.samples == planar.samples, "Interleaved and planar stereo must match")
        let mono = Capture()
        feed(mono, [[0.5, .nan, .infinity]], channels: [1])
        precondition(mono.samples[0] == mono.samples[1] && mono.samples[0][0] == 0.5 && mono.samples[0][1] == 0 && mono.samples[0][2] == 0)
        let multichannel = Capture()
        feed(multichannel, [[0.25, -0.5, 1, 1], [0.9, 0.9]], channels: [4, 2])
        precondition(multichannel.samples[0][0] == 0.25 && multichannel.samples[1][0] == -0.5)
        let ring = Capture()
        feed(ring, [Array(repeating: Float(0.125), count: 2050)], channels: [1])
        precondition(ring.position == 2 && ring.samples[0].allSatisfy { $0 == 0.125 })
        let empty = Capture()
        feed(empty, [[]], channels: [0])
        precondition(empty.position == 0)
        // A saved, disconnected output must fail instead of silently capturing another device.
        do {
            try OutputTap().start(uid: "rgbroom-test-missing-output") { _ in }
            fatalError("Missing output unexpectedly opened")
        } catch { precondition(error.localizedDescription.contains("недоступен") || error.localizedDescription.contains("14.2")) }
        print("PASS: native audio stereo/mono/multichannel buffers, finite samples, ring wrap, missing output")
    }
}
